using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// K3b (bandits dev, file-only, NOT applied): equipping the bow item in the main hand puts the player in BOW mode, equipping anything else (or unequipping) puts the sword back.
// Smallest route: no change in the equip path itself. GearSlots already raises the static events OnEquipped / OnUnequipped; this subscribes and calls the one new seam BowHooks.SetFor.
// Default OFF: bow.json equipItemId "" (or the key absent, or the master bow off) = nothing happens, old behaviour byte for byte. Key 5 / L3 keeps working as before.
// After a scene (re)load the bow mode state is cleared by BowHooks, so once the loadout is applied the equipped item is read again (resync, 2 s after the load).
public static class BowEquip
{
    static bool on;
    static string Id() { var d = BowCfg.Get(); return d != null && d.equipItemId != null ? d.equipItemId : ""; }
    static bool Armed() { return BowCfg.Master && Id().Length > 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (on) return; on = true;
        GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; SceneManager.sceneLoaded += OnScene;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { if (on) { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; SceneManager.sceneLoaded -= OnScene; on = false; } }

    static DuelFighter Fighter(GearSlots w) { if (w == null) return null; var f = w.GetComponent<DuelFighter>(); return f != null ? f : w.GetComponentInParent<DuelFighter>(); }
    static void OnEq(GearSlots w, GearTable.Item it, string slot)
    {
        if (!Armed() || it == null || slot != "mainHand") return;
        var f = Fighter(w); if (f == null || !f.IsPlayer) return;
        BowHooks.SetFor(f, it.id == Id(), "equip " + it.id);
    }
    static void OnUneq(GearSlots w, GearTable.Item it, string slot)
    {
        if (!Armed() || slot != "mainHand") return;
        var f = Fighter(w); if (f == null || !f.IsPlayer) return;
        BowHooks.SetFor(f, false, "unequip");
    }
    static void OnScene(Scene s, LoadSceneMode m) { if (Armed()) Resync.Run(); }

    sealed class Resync : MonoBehaviour
    {
        public static void Run() { var go = new GameObject("BowEquipResync"); go.hideFlags = HideFlags.HideInHierarchy; go.AddComponent<Resync>(); }
        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2f);
            foreach (var w in FindObjectsByType<GearSlots>(FindObjectsSortMode.None)) { var f = Fighter(w); if (f != null && f.IsPlayer && w.ItemIn("mainHand") == Id()) BowHooks.SetFor(f, true, "resync"); }
            Destroy(gameObject);
        }
    }
}
