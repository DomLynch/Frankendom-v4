using System;
using UnityEngine;

// K30 GEARREADY. LoadingCard.Update asks Poll() once its own conditions (prewarm, calm frames, StartupWarm) are met; the card then stays up until the player's GearSlots have all Resolved
// (visual ok or a resolved failure), but never longer than gear.json gearReadyCapS (<= 0 = no gate). Begin() (card armed) preloads the player's loadout glbs through the GlbPreload path (gear.json gearPreload).
// Everything is data (gear.json); a missing GearTable / disabled gear = ready at once. Logs one line: [GearReady] n slots, ms to ready, capped yes/no.
public static class GearReady
{
    static float beganAt = -1f, waitFrom = -1f; static bool logged; static GearSlots player;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { beganAt = waitFrom = -1f; logged = false; player = null; }

    public static void Begin(float now)
    {
        beganAt = now; waitFrom = -1f; logged = false; player = null;
        try
        {
            var G = GearTable.Get(); if (!G.enabled || !G.gearPreload) return;
            var lo = GearTable.Loadout(Roster.PlayerId); if (lo == null) return;
            int started = 0;
            for (int i = 0; i < lo.Length; i++)
            {
                var it = GearTable.Find(lo[i]); if (it == null || it.layer.Length > 0) continue;
                if (it.resource.Length == 0 && it.glb.Length > 0 && GlbPreload.Ensure(GlbPreload.FindFile(it.glb))) started++;   // an item with a Resources prefab never loads its glb
                if (it.IsPair && it.glbMirror.Length > 0 && GlbPreload.Ensure(GlbPreload.FindFile(it.glbMirror))) started++;
            }
            if (started > 0) Debug.Log("[GearReady] preloading " + started + " loadout glbs during the loading card");
        }
        catch (Exception e) { Debug.LogWarning("[GearReady] preload skipped: " + e.Message); }
    }

    /// <summary>True when the card may go: gear resolved, capped, or the gate is off.</summary>
    public static bool Poll(float now)
    {
        float cap = 3f; bool enabled = true;
        try { var G = GearTable.Get(); enabled = G.enabled; cap = G.gearReadyCapS; } catch (Exception) { }
        if (!enabled || cap <= 0f) return true;
        if (waitFrom < 0f) waitFrom = now;   // the cap counts from the moment the card would otherwise leave
        if (player == null)
        {
            foreach (var g in UnityEngine.Object.FindObjectsByType<GearSlots>(FindObjectsSortMode.None)) if (g != null && Roster.IsPlayer(g.CharacterId)) { player = g; break; }
        }
        int n = 0; bool ready = player != null && player.AllResolved(out n);
        bool capped = !ready && now - waitFrom >= cap;
        if (!ready && !capped) return false;
        if (!logged) { logged = true; Debug.Log("[GearReady] " + n + " slots, " + Mathf.RoundToInt((now - (beganAt < 0f ? waitFrom : beganAt)) * 1000f) + " ms to ready, capped " + (capped ? "yes" : "no")); }
        return true;
    }
}
