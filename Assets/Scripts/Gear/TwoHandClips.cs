using System;
using System.Collections.Generic;
using UnityEngine;

// FK3 (unity-dev): per-weapon two-hand clip sets for Joan. Data: Assets/Data/two_hand_clips.json (twin in StreamingAssets). No file / enabled false / a weapon with no row / a missing clip = the old controller, byte for byte.
// On GearSlots.OnEquipped (mainHand, player) of a weapon that has a row, an AnimatorOverrideController replaces the controller clips named in the set's map (idle, walk, guard_idle, atk_light_R, atk_heavy) with the baked set clips
// (th_<set>_<state>, states added to Joan.controller by Editor TwoHandClipSetup so they ship in the player). Any other mainHand item / unequip puts the original controller back. Lengths are identical, so attack timing data is unchanged.
public static class TwoHandClips
{
    [Serializable] public class Pair { public string from = "", to = ""; public bool loop; }
    [Serializable] public class Set { public string id = "", glb = ""; public Pair[] map = new Pair[0]; }
    [Serializable] public class Row { public string weapon = "", set = ""; }
    [Serializable] public class Data { public bool enabled = true; public Set[] sets = new Set[0]; public Row[] rows = new Row[0]; }

    static Data data; static bool on;
    public static Data Get()
    {
        if (data != null) return data;
        data = new Data();
        string json = DataPath.ReadText("two_hand_clips.json");
        if (string.IsNullOrEmpty(json)) return data;
        try { var d = JsonUtility.FromJson<Data>(json.TrimStart('\uFEFF')); if (d != null) { if (d.sets == null) d.sets = new Set[0]; if (d.rows == null) d.rows = new Row[0]; data = d; } }
        catch (Exception ex) { Debug.LogWarning("[TwoHandClips] two_hand_clips.json parse failed, old clips stay: " + ex.Message); data = new Data { enabled = false }; }
        return data;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { if (on) { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; on = false; } data = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (on) return; on = true; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; }

    sealed class Holder : MonoBehaviour { public RuntimeAnimatorController orig; public AnimatorOverrideController oc; public string set = ""; }

    static Animator AnimOf(DuelFighter f) { var fx = f != null ? f.GetComponent<Fighter>() : null; return fx != null ? fx.anim : null; }
    static DuelFighter Fighter(GearSlots w) { if (w == null) return null; var f = w.GetComponent<DuelFighter>(); return f != null ? f : w.GetComponentInParent<DuelFighter>(); }
    static void OnEq(GearSlots w, GearTable.Item it, string slot)
    {
        if (slot != "mainHand") return;
        var f = Fighter(w); if (f == null || !f.IsPlayer || AnimOf(f) == null) return;
        var d = Get(); Set s = null;
        if (d.enabled && it != null) { foreach (var r in d.rows) if (r != null && r.weapon == it.id) foreach (var c in d.sets) if (c != null && c.id == r.set) s = c; }
        if (s == null) { Restore(f, it != null ? it.id : ""); return; }
        Apply(f, s, it.id);
    }
    static void OnUneq(GearSlots w, GearTable.Item it, string slot)
    {
        if (slot != "mainHand") return;
        var f = Fighter(w); if (f == null || !f.IsPlayer || AnimOf(f) == null) return;
        Restore(f, "unequip");
    }

    static void Apply(DuelFighter f, Set s, string weapon)
    {
        var anim = AnimOf(f); var h = anim.GetComponent<Holder>();
        RuntimeAnimatorController baseCtrl = h != null && h.orig != null ? h.orig : anim.runtimeAnimatorController; if (baseCtrl == null) return;
        var byName = new Dictionary<string, AnimationClip>(); foreach (var c in baseCtrl.animationClips) if (c != null && !byName.ContainsKey(c.name)) byName[c.name] = c;
        var oc = new AnimatorOverrideController(baseCtrl); oc.name = baseCtrl.name;
        var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(); oc.GetOverrides(list);
        int n = 0, miss = 0;
        foreach (var p in s.map)
        {
            AnimationClip to; if (!byName.TryGetValue(p.to, out to)) { miss++; continue; }
            for (int i = 0; i < list.Count; i++) if (list[i].Key != null && list[i].Key.name == p.from) { list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, to); n++; }
        }
        if (n == 0) { Debug.LogWarning("[TwoHandClips] " + weapon + " set " + s.id + ": no clip replaced (missing " + miss + "), old controller stays"); return; }
        oc.ApplyOverrides(list);
        if (h == null) h = anim.gameObject.AddComponent<Holder>();
        h.orig = baseCtrl; h.oc = oc; h.set = s.id; anim.runtimeAnimatorController = oc;
        Debug.Log("[TwoHandClips] Joan " + weapon + " -> set " + s.id + ": " + n + " clip(s) replaced" + (miss > 0 ? ", " + miss + " missing" : ""));
    }
    static void Restore(DuelFighter f, string why)
    {
        var h = AnimOf(f).GetComponent<Holder>(); if (h == null || h.orig == null || h.set.Length == 0) return;
        AnimOf(f).runtimeAnimatorController = h.orig; var old = h.set; h.set = ""; h.oc = null;
        Debug.Log("[TwoHandClips] Joan " + why + ": set " + old + " off, original controller back");
    }
}