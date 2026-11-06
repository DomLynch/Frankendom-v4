using UnityEngine;

// V3 armfeel Flinch (armfeel.ts class Flinch + armfeel-defs.json): a landed blow leans the victim's visual pivot about its base and nudges it along the blow;
// energy starts at `gain` per blow, holds 45 ms (kill 80 ms), then decays exponentially (12/s, kill 10/s). lean .45 rad (kill .9), nudge .16 m.
// FLINCH_GAIN: hero .25, opponent 1. Presentation only: the root, the CharacterController and the sim never move. Runs on unscaled time.
public class Flinch : MonoBehaviour
{
    public Transform pivot;   // the "Visual" parent of the rig
    public float gain = 1f;
    const float Lean = 0.45f, LeanKill = 0.9f, Nudge = 0.16f, Decay = 12f, DecayKill = 10f, Hold = 0.045f, HoldKill = 0.08f;
    float energy, hold; bool kill; Vector3 dir; DuelFighter me;

    void OnEnable() { ContactRelay.OnEvent += OnEvent; me = GetComponent<DuelFighter>(); }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; if (pivot) { pivot.localPosition = Vector3.zero; pivot.localRotation = Quaternion.identity; } }

    void OnEvent(CombatEvent e)
    {
        if (e.type != "Hit" || e.target != me || e.actor == null) return;
        dir = me.transform.position - e.actor.transform.position; dir.y = 0f; dir.Normalize();
        kill = me.Dead; energy = gain; hold = kill ? HoldKill : Hold;
    }

    void LateUpdate()
    {
        if (!pivot) return;
        float dt = Time.unscaledDeltaTime;
        if (energy <= 0f) { pivot.localPosition = Vector3.zero; pivot.localRotation = Quaternion.identity; return; }
        float spent = Mathf.Min(hold, dt); hold -= spent;
        energy *= Mathf.Exp(-(kill ? DecayKill : Decay) * Mathf.Max(0f, dt - spent));
        if (energy < 0.001f) { energy = 0f; return; }
        float lean = (kill ? LeanKill : Lean) * energy * Mathf.Rad2Deg;
        if (!kill) lean = Mathf.Min(lean, DodgeTable.Get().flinchMaxLeanDeg);   // data cap (Goose QC: <= 12 deg)
        lean *= HitReact.LeanScale(me);   // PHYS2 v1: the partial hit ragdoll already sways the body; keep less of the visual-pivot lean while it shows (physics.json hit.flinchLeanScale)
        Vector3 axis = Vector3.Cross(Vector3.up, dir).normalized;   // lean back along the blow
        pivot.rotation = Quaternion.AngleAxis(lean, axis) * transform.rotation;
        Vector3 world = dir * Nudge * energy; pivot.position = transform.position + world;
    }
}
