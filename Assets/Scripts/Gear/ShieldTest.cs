#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using UnityEngine;

// SHIELD v1c QA harness (laneSHIELD): `-shield:test` makes every fighter that wears a shield hold its SHIELD-SIDE guard (gear.json shield.sideGuard) for the length of any enemy swing whose
// direction is one of the shield's cover attacks (overhead / thrust), so the extra-cover rule is exercised deterministically. The balance bot guards the centre unless it reads a side
// (AIBrain.cs:529 SideIdFor(it.guardDir), guardDir null = centre), so it never held the shield side by itself: 0 `[Shield] extra-cover block` lines in 20 fights is the bot's choice, the rule is wired.
// Off without the flag; changes no data and no path of the rule itself.
// Proof: run -balanceauto (or a duel) with `-shield:test -shield:log` and count the `[Shield] extra-cover block` lines; each forced swing also logs `[Shield] TEST`.
public static class ShieldTest
{
    static bool on; static int forced;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        on = false; forced = 0; foreach (var a in Environment.GetCommandLineArgs()) if (a == "-shield:test") on = true;
        ContactRelay.OnAttackStarted -= OnAttack; if (!on) return;
        ContactRelay.OnAttackStarted += OnAttack; Debug.Log("[Shield] TEST harness on: shield wearers hold the shield-side guard against swings the shield covers");
    }
    static void OnAttack(DuelFighter a, MoveDef m)
    {
        if (a == null || m == null) return;
        foreach (var d in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (d == null || d == a || d.Dead) continue;
            var s = ShieldHooks.Of(d); if (s == null || s.cover == null || string.IsNullOrEmpty(m.direction)) continue;
            bool covered = false; for (int i = 0; i < s.cover.Length; i++) if (s.cover[i] != null && s.cover[i].attacks != null && Array.IndexOf(s.cover[i].attacks, m.direction) >= 0) covered = true;
            if (!covered) continue;
            int id = DirectionalGuard.SideIdFor(s.sideGuard); if (id <= 0) continue;
            var keep = d.gameObject.GetComponent<ShieldTestKeep>(); if (keep == null) keep = d.gameObject.AddComponent<ShieldTestKeep>();
            keep.fighter = d; keep.side = id; keep.until = Duel.TickCount + 90; d.guardSide = id; d.FlowForceGuard(90); forced++;
            if (forced <= 40) Debug.Log("[Shield] TEST #" + forced + ": " + d.name + " holds guard " + DirectionalGuard.NameOf(id) + " vs " + a.name + " " + m.id + " (" + m.direction + ")");
        }
    }
}
// keeps the side asserted every frame until the swing is over (the AI would otherwise overwrite guardSide at its next decision)
public class ShieldTestKeep : MonoBehaviour
{
    public DuelFighter fighter; public int side, until;
    void LateUpdate() { if (fighter == null || Duel.TickCount > until) return; fighter.guardSide = side; }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
