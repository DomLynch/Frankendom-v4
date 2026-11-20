using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// BOWCARRY v1 (laneLG, file-only patch, NOT applied, UNPROVEN IN UNITY). OpenTrident's carry / nock / nocked-ready / club-swing clips (kits_out\anim\npc_bow_clips\nock_carry_20261120) played WITHOUT touching the baked Animator controller.
// Same method as BowWalk (runtime PlayableGraph on the fighter's Animator): input 0 = the legs of the SAME controller (the locomotion state DuelFighter.Update just played), inputs 1 and 2 = the bow clip on the upper-body AvatarMask (BowWalk.MaskFor), cross-faded.
// The clips are glb files under StreamingAssets, loaded once with glTFast (same loader as NpcBowAnim). Every path and number is bow_carry_clips.json; no file / enabled false / a missing clip = today, byte for byte.
//  Joan, bow mode up, state Loco: no foe = CARRY loop; foe engaged and arrows left = NOCK (0.53 s, the arrow appears on the string at row.eventS) then READY loop. Any other state: the graph is gone and the Animator owns the pose (the baked bow_loose shot).
//  Joan, melee press with the bow and the foe inside club.rangeM: DuelFighter.Press hands the press to the move club.moveId (a shove.json move row, so the existing ShoveTable start / hit / knock path runs it; damage and timing = move-defs.json),
//  and while that move runs the CLUB clip is on the upper body.
//  NPC archers (52-bone rig): CARRY while the bow is shown and he is not aiming; the nock-before-draw lives in NpcBowAnim (Set.nock).
// No serialized field, no RuntimeInitializeOnLoadMethod: Reset() is called from BowHooks.ResetStatics / OnScene like BowWalk.Reset.
public static class BowCarry
{
    [Serializable] public class Row { public string id = "", role = "", rig = "", glb = "", anim = ""; public bool loop; public float fadeInS = 0.12f, eventS = 0f, resumeS = 0f, hitFromS = 0f, hitToS = 0f, speed = 1f; }
    [Serializable] public class Club { public bool enabled; public string moveId = "bow_club"; public float rangeM = 1.8f; public string[] fromPresses = new string[0]; public bool playerOnly = true; public bool logWrist = true; }
    [Serializable] public class Data { public bool enabled, log = true, npc = true; public string joanRig = "joan", npcRig = "npc52"; public Row[] rows = new Row[0]; public Club club = new Club(); }

    static Data data;
    static readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
    static readonly HashSet<string> busy = new HashSet<string>();
    static readonly Dictionary<string, float> retryAt = new Dictionary<string, float>();
    static readonly Dictionary<DuelFighter, Runner> runners = new Dictionary<DuelFighter, Runner>();

    public static Data Get()
    {
        if (data != null) return data;
        data = new Data(); string json = DataPath.ReadText("bow_carry_clips.json"); if (string.IsNullOrEmpty(json)) return data;
        try { var parsed = JsonUtility.FromJson<Data>(json.TrimStart('﻿')); if (parsed != null) { if (parsed.rows == null) parsed.rows = new Row[0]; if (parsed.club == null) parsed.club = new Club(); if (parsed.club.fromPresses == null) parsed.club.fromPresses = new string[0]; data = parsed; } }
        catch (Exception ex) { Debug.LogWarning("[BowCarry] bow_carry_clips.json parse failed, today's poses stay: " + ex.Message); data = new Data(); }
        return data;
    }
    public static bool On { get { return Get().enabled; } }
    public static Row RowOf(string id) { var all = Get(); for (int i = 0; i < all.rows.Length; i++) if (all.rows[i] != null && all.rows[i].id == id) return all.rows[i]; return null; }
    public static float EventS(string id, float fallback) { var found = RowOf(id); return found != null && found.eventS > 0f ? found.eventS : fallback; }
    public static float ResumeS(string id) { var found = RowOf(id); return found != null ? found.resumeS : 0f; }

    // called from the existing BowHooks.ResetStatics / OnScene
    public static void Reset() { foreach (var pair in runners) if (pair.Value != null) pair.Value.Stop(); runners.Clear(); }

    // ---------------------------------------------------------------- clip loading (glTFast, once per row, retried after 10 s on a failure)
    static AnimationClip ClipOf(Row spec)
    {
        AnimationClip have; if (clips.TryGetValue(spec.id, out have)) return have; float again;
        if (busy.Contains(spec.id) || (retryAt.TryGetValue(spec.id, out again) && Time.unscaledTime < again)) return null;
        busy.Add(spec.id); Load(spec); return null;
    }
    static async void Load(Row spec)
    {
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, spec.glb.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { Debug.LogWarning("[BowCarry] missing " + path + " (row " + spec.id + "), retry in 10 s"); retryAt[spec.id] = Time.unscaledTime + 10f; return; }
            var import = new GLTFast.GltfImport(); if (!await import.LoadFile(path)) { retryAt[spec.id] = Time.unscaledTime + 10f; return; }
            var found = import.GetAnimationClips(); AnimationClip pick = null;
            if (found != null) foreach (var one in found) { if (one == null) continue; if (one.name == spec.anim) { pick = one; break; } if (pick == null) pick = one; }
            if (pick == null) { Debug.LogWarning("[BowCarry] no clip '" + spec.anim + "' in " + spec.glb + ", retry in 10 s"); retryAt[spec.id] = Time.unscaledTime + 10f; return; }
            pick.legacy = false; pick.wrapMode = spec.loop ? WrapMode.Loop : WrapMode.Once; clips[spec.id] = pick;
            if (Get().log) Debug.Log("[BowCarry] clip loaded: " + spec.id + " (" + pick.name + ") " + pick.length.ToString("F2") + " s from " + spec.glb);
        }
        catch (Exception ex) { Debug.LogWarning("[BowCarry] load failed (" + spec.id + "): " + ex.Message + ", retry in 10 s"); retryAt[spec.id] = Time.unscaledTime + 10f; }
        finally { busy.Remove(spec.id); }
    }

    // ---------------------------------------------------------------- the seams
    /// <summary>DuelFighter.Press (first lines): a melee press with the bow up and the foe close becomes the club move. True = the press was handed on, the caller returns.</summary>
    public static bool Redirect(DuelFighter fighter, string what)
    {
        var all = Get(); var club = all.club; if (!all.enabled || !club.enabled || fighter == null || string.IsNullOrEmpty(club.moveId) || (club.playerOnly && !fighter.IsPlayer)) return false;
        if (System.Array.IndexOf(club.fromPresses, what) < 0 || !BowHooks.Active(fighter)) return false;
        var foe = fighter.opponent; if (foe == null || foe.Dead) return false;
        Vector3 gap = foe.transform.position - fighter.transform.position; gap.y = 0f; if (gap.magnitude > club.rangeM) return false;
        if (all.log) Debug.Log("[BowCarry] melee press '" + what + "' with the bow at " + gap.magnitude.ToString("F2") + " m -> " + club.moveId);
        fighter.Press(club.moveId); return true;
    }

    /// <summary>BowVisual.Nock: the arrow sits on the string during the nock (from row.eventS) and the nocked-ready loop.</summary>
    public static bool ArrowOn(DuelFighter fighter) { Runner run; return fighter != null && runners.TryGetValue(fighter, out run) && run.ArrowOn; }

    static bool IsArcher(DuelFighter fighter) { return fighter != null && !fighter.IsPlayer && fighter.GetComponent<BowVisual>() != null && BanditArcher.TypeOf(fighter.CharacterId) != null; }

    /// <summary>DuelFighter.Update (per rendered frame, after BowWalk.Present): keep the bow pose over the locomotion legs.</summary>
    public static void Present(DuelFighter fighter, Fighter body, float dt)
    {
        if (fighter == null) return;
        Runner run; runners.TryGetValue(fighter, out run); if (run != null && !run.Alive) { runners.Remove(fighter); run = null; }   // a failed graph is dropped and rebuilt
        var all = Get(); if (!all.enabled || body == null || body.anim == null) { if (run != null) { run.Stop(); runners.Remove(fighter); } return; }
        string role, rig; Pick(fighter, out role, out rig);
        if (role.Length == 0) { if (run != null) { run.Stop(); runners.Remove(fighter); } return; }
        string target = role;
        if (role == "ready")   // engaged: nock first (carry -> nock -> ready), a finished nock hands over to ready
        {
            string now = run != null ? run.role : "";
            if (now == "nock") target = run.NockDone ? "ready" : "nock"; else if (now != "ready" && RowOf(rig + "_nock") != null) target = "nock";
        }
        if (run != null && run.role == target) { run.Tick(fighter, body, dt); return; }
        var spec = RowOf(rig + "_" + target); AnimationClip clip = spec != null ? ClipOf(spec) : null;
        if (clip == null) { if (run != null) run.Tick(fighter, body, dt); return; }   // not loaded yet: the Animator keeps the pose until it is
        if (run == null) { run = new Runner(); if (!run.Start(body)) return; runners[fighter] = run; }
        run.Put(spec, clip, target);
        if (all.log) Debug.Log("[BowCarry] " + fighter.name + " " + rig + " -> " + target + " (" + spec.id + ")");
        run.Tick(fighter, body, dt);
    }

    // which clip role this fighter wants this frame ("" = none: the Animator owns the pose)
    static void Pick(DuelFighter fighter, out string role, out string rig)
    {
        var all = Get(); role = ""; rig = ""; if (fighter.Dead || Duel.FrozenFor(fighter)) return;
        if (fighter.IsPlayer)
        {
            if (!BowHooks.Active(fighter)) return; rig = all.joanRig; var club = all.club;
            if (club.enabled && fighter.CurrentMove != null && fighter.CurrentMove.id == club.moveId && (fighter.state == FState.Windup || fighter.state == FState.Active || fighter.state == FState.Recovery)) { role = "club"; return; }
            if (fighter.state != FState.Loco || fighter.guardHeld) return;
            bool bowUp; int ammo; DuelFighter foe; if (!BowHooks.TryHud(fighter, out bowUp, out ammo, out foe)) return;
            bool engaged = foe != null && !foe.Dead && (ammo > 0 || !BowCfg.Get().quiverOn);
            role = engaged ? "ready" : "carry"; return;
        }
        if (!all.npc || !IsArcher(fighter) || fighter.state != FState.Loco) return;
        var archer = fighter.GetComponent<BanditArcher>(); if (archer == null || archer.Aiming || archer.BowClipAlive) return;
        if (NpcBowAnim.KindOf(fighter.CharacterId) != "52") return;   // Mixamo-106 archers (Slag) have no carry rows: they keep the controller
        rig = all.npcRig; role = "carry";
    }

    // ---------------------------------------------------------------- one fighter's graph
    sealed class Runner
    {
        PlayableGraph graph; AnimatorControllerPlayable legs; AnimationLayerMixerPlayable mix;
        readonly AnimationClipPlayable[] slot = new AnimationClipPlayable[2]; readonly bool[] used = new bool[2]; readonly float[] weight = new float[2];
        int top = -1; float fadeStep = 8f, startT, nockLen; bool alive, loggedStart, loggedEnd; string legState = ""; Row spec; Transform wrist;
        public string role = "";
        public bool Alive { get { return alive; } }
        public bool ArrowOn { get { return alive && (role == "ready" || (role == "nock" && spec != null && Time.time - startT >= (spec.eventS > 0f ? spec.eventS : 0.35f))); } }
        public bool NockDone { get { return role != "nock" || Time.time - startT >= nockLen; } }

        public bool Start(Fighter body)
        {
            var an = body.anim; var ctrl = an.runtimeAnimatorController; if (ctrl == null) return false;
            try
            {
                graph = PlayableGraph.Create("BowCarry"); graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var outp = AnimationPlayableOutput.Create(graph, "BowCarryOut", an);
                legs = AnimatorControllerPlayable.Create(graph, ctrl); mix = AnimationLayerMixerPlayable.Create(graph, 3);
                graph.Connect(legs, 0, mix, 0); mix.SetInputWeight(0, 1f); var mask = BowWalk.MaskFor(an, BowCfg.Get().move);
                mix.SetLayerMaskFromAvatarMask(1u, mask); mix.SetLayerMaskFromAvatarMask(2u, mask);
                string cur = body.Current; legState = !string.IsNullOrEmpty(cur) && legs.HasState(0, Animator.StringToHash(cur)) ? cur : "idle"; legs.Play(Animator.StringToHash(legState), 0, 0f);
                outp.SetSourcePlayable(mix); graph.Play(); alive = true; return true;
            }
            catch (Exception ex) { Debug.LogWarning("[BowCarry] graph failed, the Animator keeps the pose: " + ex.Message); Stop(); return false; }
        }
        public void Put(Row next, AnimationClip clip, string newRole)
        {
            if (!alive || !graph.IsValid()) return;
            try
            {
                int free = top < 0 ? 0 : 1 - top; if (used[free]) { graph.Disconnect(mix, free + 1); if (slot[free].IsValid()) slot[free].Destroy(); used[free] = false; }
                float rate = next.speed > 0f ? next.speed : 1f;
                var play = AnimationClipPlayable.Create(graph, clip); play.SetDuration(next.loop ? double.MaxValue : clip.length); play.SetSpeed(rate);
                graph.Connect(play, 0, mix, free + 1); slot[free] = play; used[free] = true; weight[free] = top < 0 ? 1f : 0f; mix.SetInputWeight(free + 1, weight[free]);
                top = free; fadeStep = 1f / Mathf.Max(0.02f, next.fadeInS); spec = next; role = newRole; startT = Time.time; nockLen = newRole == "nock" ? clip.length / rate : 0f; loggedStart = loggedEnd = false;
            }
            catch (Exception ex) { Debug.LogWarning("[BowCarry] put failed: " + ex.Message); Stop(); }
        }
        public void Tick(DuelFighter fighter, Fighter body, float dt)
        {
            if (!alive || !graph.IsValid()) return;
            try
            {
                var an = body.anim; bool frozen = PresentationClock.Frozen; float rate = spec != null && spec.speed > 0f ? spec.speed : 1f;
                for (int i = 0; i < 2; i++)
                {
                    if (!used[i]) continue;
                    weight[i] = Mathf.MoveTowards(weight[i], i == top ? 1f : 0f, dt * fadeStep); mix.SetInputWeight(i + 1, weight[i]); slot[i].SetSpeed(frozen ? 0f : rate);
                    if (i != top && weight[i] <= 0f) { graph.Disconnect(mix, i + 1); if (slot[i].IsValid()) slot[i].Destroy(); used[i] = false; }
                }
                bool club = role == "club"; string want = club ? "idle" : body.Current;   // the club move stands still (MoveTick gives v = 0 in Windup / Active / Recovery); otherwise the controller's own locomotion state
                if (string.IsNullOrEmpty(want) || !legs.HasState(0, Animator.StringToHash(want))) want = "idle";
                if (want != legState) { legState = want; legs.CrossFadeInFixedTime(Animator.StringToHash(want), 0.15f, 0, 0f); }
                legs.SetSpeed(frozen ? 0f : Mathf.Max(0.05f, an.speed));
                if (club && spec != null && BowCarry.Get().club.logWrist) WristLog(fighter, body);
            }
            catch (Exception ex) { Debug.LogWarning("[BowCarry] tick failed: " + ex.Message); Stop(); }
        }
        // README: check the left wrist in the engine. One line at the start and one at the end of the hit window, the wrist in the fighter's local frame (x = its right, y = up, z = forward).
        void WristLog(DuelFighter fighter, Fighter body)
        {
            float age = Time.time - startT;
            if (wrist == null) { string bone = Roster.BoneName(fighter, "LeftHand"); foreach (var found in body.anim.GetComponentsInChildren<Transform>(true)) if (found.name == bone) { wrist = found; break; } if (wrist == null) return; }
            if (!loggedStart && age >= spec.hitFromS) { loggedStart = true; Debug.Log("[BowCarry] club wrist L at hit start t=" + age.ToString("F2") + " local " + fighter.transform.InverseTransformPoint(wrist.position).ToString("F2")); }
            if (!loggedEnd && age >= spec.hitToS) { loggedEnd = true; Debug.Log("[BowCarry] club wrist L at hit end t=" + age.ToString("F2") + " local " + fighter.transform.InverseTransformPoint(wrist.position).ToString("F2")); }
        }
        public void Stop() { if (graph.IsValid()) graph.Destroy(); alive = false; role = ""; used[0] = false; used[1] = false; top = -1; }
    }
}
