using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// FK3b (Lead, Dom 15 Nov): one-handed and two-handed weapons. A weapon item whose gear.json weapon block says "twoHanded": true (maul, great axe ...) needs both hands, so equipping it takes the shield (the item in the off-hand slot) off;
// a one-handed weapon (sword, knife ...) leaves the off-hand alone and the shield takes it. Equipping a shield while a two-hander is wielded puts the two-hander away (the shield takes the off-hand).
// two_hand_rule.json (StreamingAssets first, then Assets/Data): enabled, mainSlot, offSlot, restoreOffHand (when she goes back to a one-hander / the default sword, the shield that was taken off goes back on).
// Missing file or "enabled": false = old behaviour byte for byte. Player only (the GearSlots of the player fighter).
public static class TwoHandRule
{
    [Serializable] public class Cfg { public bool enabled = false, log = true, restoreOffHand = true; public string mainSlot = "mainHand", offSlot = "offHand"; }
    static Cfg c; static bool on, busy; static readonly Dictionary<GearSlots, string> displaced = new Dictionary<GearSlots, string>();
    static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "two_hand_rule.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/two_hand_rule.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[TwoHandRule] read failed, off: " + e.Message); c = new Cfg(); }
        return c;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { if (on) { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; on = false; } c = null; busy = false; displaced.Clear(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (on || !Get().enabled) return; on = true; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; }

    static bool IsPlayer(GearSlots gs) { if (gs == null) return false; var f = gs.GetComponent<DuelFighter>(); if (f == null) f = gs.GetComponentInParent<DuelFighter>(); return f != null && f.IsPlayer; }
    static bool TwoHanded(GearTable.Item it) { return it != null && it.weapon != null && it.weapon.twoHanded; }

    static void OnEq(GearSlots gs, GearTable.Item it, string slot)
    {
        var k = Get(); if (busy || !k.enabled || it == null || !IsPlayer(gs)) return;
        busy = true;
        try
        {
            if (slot == k.mainSlot)
            {
                if (TwoHanded(it))
                {
                    string off = gs.ItemIn(k.offSlot);
                    if (!string.IsNullOrEmpty(off)) { displaced[gs] = off; gs.Unequip(k.offSlot); if (k.log) Debug.Log("[TwoHandRule] " + it.id + " is two-handed: " + off + " taken off"); }
                }
                else RestoreOff(gs, k, "one-handed " + it.id);
            }
            else if (slot == k.offSlot)
            {
                var mainIt = GearTable.Find(gs.ItemIn(k.mainSlot));
                if (TwoHanded(mainIt)) { string m = mainIt.id; gs.Unequip(k.mainSlot); displaced.Remove(gs); if (k.log) Debug.Log("[TwoHandRule] " + it.id + " takes the off-hand: two-handed " + m + " put away"); }
            }
        }
        finally { busy = false; }
    }
    static void OnUneq(GearSlots gs, GearTable.Item it, string slot)
    {
        var k = Get(); if (busy || !k.enabled || !IsPlayer(gs) || slot != k.mainSlot) return;
        busy = true; try { RestoreOff(gs, k, "main hand empty"); } finally { busy = false; }
    }
    static void RestoreOff(GearSlots gs, Cfg k, string why)
    {
        string id; if (!k.restoreOffHand || !displaced.TryGetValue(gs, out id)) return;
        displaced.Remove(gs); if (!string.IsNullOrEmpty(gs.ItemIn(k.offSlot))) return;
        gs.Equip(id, k.offSlot); if (k.log) Debug.Log("[TwoHandRule] " + why + ": " + id + " back on");
    }
}
