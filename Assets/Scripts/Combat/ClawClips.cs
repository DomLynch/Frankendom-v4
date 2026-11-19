using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// W8 (fk-W8, 18 Nov): the claw bodies (NpcCrocWarrior, NpcBogHag, NpcBarkSentinel, NpcRootDruid) have attack_clips.json rows that name gcl_* states, but their Animator controllers only carry the base 30-clip set, so the rows fell back to the sword clips.
// The seven gcl_* clips are not in the player (unreferenced GLB sub-clips are not shipped). They sit in StreamingAssets/Npc/claw_clips.glb (clips-only extract of NpcCrocWarrior_combat.glb, identical in all four bodies), loaded once with glTFast (same loader as NpcBowAnim).
// Per claw fighter the controller is wrapped in an AnimatorOverrideController: each gcl clip replaces a DONOR clip of the controller (claw_clips.json map: gcl state -> donor state/clip name). BeginAttackClip asks Donor() when the row's gcl state is missing and plays the donor state with the row's own claw timings.
// No serialized fields, no new RuntimeInitializeOnLoadMethod (called from DuelFighter.BeginAttackClip), no scene change: compile-only. Data: claw_clips.json (enabled false / file missing / character not listed / clips not loaded yet / path check fails = the old fallback sword clip, byte for byte).
public static class ClawClips
{
    [Serializable] public class Pair { public string state = "", donor = ""; }
    [Serializable] public class Data { public bool enabled = true; public bool debugLog = true; public string glb = "Npc/claw_clips.glb"; public string[] characters = new string[0]; public Pair[] map = new Pair[0]; }

    static Data data;
    static readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();   // gcl state -> clip (named after its donor)
    static bool loading, loaded; static float retryAt;
    static System.Runtime.CompilerServices.ConditionalWeakTable<Animator, Dictionary<string, string>> done = new System.Runtime.CompilerServices.ConditionalWeakTable<Animator, Dictionary<string, string>>();   // animator -> (gcl state -> donor); empty = checked, nothing bound

    public static Data Get()
    {
        if (data != null) return data;
        data = new Data();
        string json = DataPath.ReadText("claw_clips.json");
        if (string.IsNullOrEmpty(json)) { data.enabled = false; return data; }
        try { var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿')); if (d != null) { if (d.map == null) d.map = new Pair[0]; if (d.characters == null) d.characters = new string[0]; data = d; } }
        catch (Exception ex) { Debug.LogWarning("[ClawClips] claw_clips.json parse failed, sword fallback stays: " + ex.Message); data = new Data { enabled = false }; }
        return data;
    }

    static bool Listed(Data d, string character)
    {
        if (string.IsNullOrEmpty(character)) return false;
        for (int i = 0; i < d.characters.Length; i++) if (d.characters[i] == character) return true;
        return false;
    }

    /// <summary>The donor state to play for a row whose gcl state is missing in this fighter's controller, or null (keep the row's fallback clip).</summary>
    public static string Donor(Animator an, string character, string gclState)
    {
        try
        {
            var d = Get(); if (!d.enabled || an == null || string.IsNullOrEmpty(gclState) || !Listed(d, character)) return null;
            Dictionary<string, string> m;
            if (!done.TryGetValue(an, out m))
            {
                if (!loaded) { Preload(d); return null; }   // first swing before the 244 KB clip file is in: this one keeps the fallback, the next one has the claws
                m = Apply(d, an, character); done.Add(an, m);
            }
            string donor; return m.TryGetValue(gclState, out donor) ? donor : null;
        }
        catch (Exception e) { Debug.LogWarning("[ClawClips] " + e.Message); return null; }
    }

    public static float Length(string gclState) { AnimationClip c; return clips.TryGetValue(gclState ?? "", out c) && c != null ? c.length : 0f; }

    /// <summary>Starts the one-time clip load (safe to call early, e.g. when a claw body spawns).</summary>
    public static void Preload(Data d)
    {
        if (loaded || loading || Time.unscaledTime < retryAt) return;
        loading = true; Load(d);
    }

    static async void Load(Data d)
    {
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, d.glb);
            if (!File.Exists(p)) { Debug.LogWarning("[ClawClips] missing " + p + ": claw bodies keep the sword fallback, retry in 30 s"); retryAt = Time.unscaledTime + 30f; return; }
            var g = new GLTFast.GltfImport();
            if (!await g.LoadFile(p)) { Debug.LogWarning("[ClawClips] load failed (" + p + "), retry in 30 s"); retryAt = Time.unscaledTime + 30f; return; }
            var cl = g.GetAnimationClips(); int n = 0;
            if (cl != null) foreach (var c in cl)
            {
                if (c == null) continue;
                string gname = c.name;
                foreach (var pr in d.map) if (pr != null && pr.state == gname)
                {
                    c.legacy = false; c.wrapMode = WrapMode.Once; c.name = pr.donor;   // ClipLen() in DuelFighter reads clip.name: the clip takes its donor's name
                    clips[pr.state] = c; n++; break;
                }
            }
            loaded = n > 0;
            if (!loaded) { retryAt = Time.unscaledTime + 30f; Debug.LogWarning("[ClawClips] no gcl clip found in " + p); }
            else if (d.debugLog) Debug.Log("[ClawClips] clips loaded from " + d.glb + ": " + n + " of " + d.map.Length);
        }
        catch (Exception e) { Debug.LogWarning("[ClawClips] load failed: " + e.Message + ", retry in 30 s"); retryAt = Time.unscaledTime + 30f; }
        finally { loading = false; }
    }

    static Dictionary<string, string> Apply(Data d, Animator an, string character)
    {
        var res = new Dictionary<string, string>();
        var baseCtrl = an.runtimeAnimatorController; if (baseCtrl == null) return res;
        if (an.transform.Find("Rig/root") == null) { Debug.LogWarning("[ClawClips] " + character + ": bone path Rig/root not found under the Animator, sword fallback stays"); return res; }
        var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        var oc = new AnimatorOverrideController(baseCtrl); oc.name = baseCtrl.name; oc.GetOverrides(list);
        int n = 0, miss = 0;
        foreach (var pr in d.map)
        {
            AnimationClip to; if (pr == null || !clips.TryGetValue(pr.state, out to)) { miss++; continue; }
            if (an.HasState(0, Animator.StringToHash(pr.state))) continue;   // the controller has the real gcl state (Grendel): untouched
            bool hit = false;
            for (int i = 0; i < list.Count; i++) if (list[i].Key != null && list[i].Key.name == pr.donor) { list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, to); hit = true; }
            if (hit) { res[pr.state] = pr.donor; n++; } else miss++;
        }
        if (n == 0) { Debug.LogWarning("[ClawClips] " + character + ": no donor clip replaced (missing " + miss + "), sword fallback stays"); return res; }
        oc.ApplyOverrides(list);
        var si = an.GetCurrentAnimatorStateInfo(0);
        an.runtimeAnimatorController = oc;
        try { an.Play(si.fullPathHash, 0, si.normalizedTime); } catch (Exception) { }   // swapping the controller restarts the state machine: resume the state we were in
        if (d.debugLog) Debug.Log("[ClawClips] " + character + ": " + n + " claw clip(s) bound over donor clips" + (miss > 0 ? ", " + miss + " not bound" : ""));
        return res;
    }

    /// <summary>Drops the per-run state (play mode restart in the editor).</summary>
    public static void Reset() { data = null; done = new System.Runtime.CompilerServices.ConditionalWeakTable<Animator, Dictionary<string, string>>(); clips.Clear(); loaded = false; loading = false; retryAt = 0f; }
}
