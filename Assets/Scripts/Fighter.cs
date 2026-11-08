using UnityEngine;

// Shared animation helper: code-driven CrossFade between the clip states, plus a ground snap so feet touch the floor.
[DefaultExecutionOrder(500)]   // after Duel/DuelFighter.Update so the freeze pin wins before the Animator evaluates
public class Fighter : MonoBehaviour
{
    public Animator anim;
    public Transform model;
    string cur = "";
    public bool holdLast;
    public float animWeight = 1f;   // heaviness multiplier on Animator speed (the enemy 0.92)
    Renderer[] rs;
    // v109d: -statelog prints every attack / claw / skill state a fighter is sent to ("[AnimState] Sobek -> claw_swipe_R"): the camtrace only has the coarse FState. Off without the flag.
    static readonly bool StateLog = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-statelog") >= 0;
    void LogState(string state) { if (StateLog && state != null && (state.StartsWith("atk_") || state.StartsWith("claw_") || state.StartsWith("skill") || state.StartsWith("fin_") || state.StartsWith("kick") || state.StartsWith("riposte") || state.StartsWith("maul_") || state.StartsWith("shove"))) Debug.Log("[AnimState] " + name + " -> " + state + " t " + Time.time.ToString("F2")); }
    public void Play(string state, float fade = 0.15f)
    {
        if (cur == state || anim == null) return;
        float d = AnimBlend.Fade(cur, state, fade);   // data-driven (anim_blend.json); returns `fade` unchanged when the table is off
        cur = state; LogState(state);
        anim.CrossFadeInFixedTime(state, d);
    }
    public void Force(string state, float fade = 0.08f)
    {
        if (anim == null) { cur = state; return; }
        float d = AnimBlend.Fade(cur, state, fade);
        cur = state; LogState(state);
        anim.CrossFadeInFixedTime(state, d, 0, 0f);
    }
    // explicit-fade variants (attack_clips.json): the caller states the crossfade seconds and the clip start in seconds, AnimBlend is not consulted
    public void ForceExact(string state, float fade, float startSeconds = 0f)
    {
        cur = state; LogState(state);
        if (anim == null) return;
        anim.CrossFadeInFixedTime(state, Mathf.Max(0f, fade), 0, Mathf.Max(0f, startSeconds));
    }
    public void PlayExact(string state, float fade)
    {
        if (cur == state || anim == null) return;
        cur = state; LogState(state);
        anim.CrossFadeInFixedTime(state, Mathf.Max(0f, fade));
    }
    public string Current => cur;

    Transform toeL, toeR; Health hp;
    // hit-stop: the single place that freezes/restores Animator.speed (DuelFighter.PlayClip etc. keep writing anim.speed freely)
    bool frozenApplied; float savedSpeed = 1f;
    void ApplyFreeze()
    {
        if (!anim) return;
        if (PresentationClock.Frozen)
        {
            if (!frozenApplied) { frozenApplied = true; savedSpeed = anim.speed; }
            else if (anim.speed != 0f) savedSpeed = anim.speed;   // the sim wrote a fresh speed this frame: keep it for the restore
            anim.speed = 0f;
        }
        else if (frozenApplied)
        {
            frozenApplied = false;
            if (anim.speed == 0f) anim.speed = savedSpeed;        // still our pin (a fresh write from the sim is left alone)
        }
    }
    // HITFX2: presentation hit-stop = a per-fighter Animator speed dip / catch-up (HitFx.PresMul); the sim clock is not touched
    bool presApplied; float presSaved = 1f, presWritten = 1f;
    void ApplyPresDip()
    {
        if (!anim || PresentationClock.Frozen) return;
        float m = HitFx.PresMul(this);
        if (m == 1f && !presApplied) return;
        if (m == 1f) { presApplied = false; if (Mathf.Approximately(anim.speed, presWritten)) anim.speed = presSaved; return; }
        if (!presApplied) { presApplied = true; presSaved = anim.speed; }
        else if (!Mathf.Approximately(anim.speed, presWritten)) presSaved = anim.speed;
        presWritten = presSaved * m; anim.speed = presWritten;
    }
    void PresRestore() { if (presApplied) { presApplied = false; if (anim && Mathf.Approximately(anim.speed, presWritten)) anim.speed = presSaved; } }
    void OnDisable() { PresRestore(); if (frozenApplied && anim) { frozenApplied = false; if (anim.speed == 0f) anim.speed = savedSpeed; } }

    void Update()
    {
        ApplyFreeze(); ApplyPresDip(); /*HITFX2*/
        if (holdLast && anim) { var si = anim.GetCurrentAnimatorStateInfo(0); if (si.normalizedTime >= 0.97f && !anim.IsInTransition(0)) anim.speed = 0f; }
    }
    void LateUpdate()
    {
        if (!model) return;
        if (toeL == null)
        {
            { string bpre = Roster.BonePrefix(this); foreach (var t in model.GetComponentsInChildren<Transform>()) { if (t.name == bpre + "LeftToe_End") toeL = t; else if (t.name == bpre + "RightToe_End") toeR = t; } } /* HCAUDIT1 v104 */
            hp = GetComponent<Health>();
            if (toeL == null || toeR == null) return;
        }
        if (hp && hp.Dead) return;
        if (!startYSet)
        {
            startY = model.localPosition.y;
            startYSet = true;
            hasFootGround = GetComponentInChildren<FootGround>(true) != null || GetComponent<FootGround>() != null || GetComponentInParent<FootGround>() != null;
            if (toeL != null && toeR != null) initialToeY = Mathf.Min(toeL.position.y, toeR.position.y) - transform.position.y;
        }
        if (hasFootGround) { var q = model.localPosition; if (Mathf.Abs(q.y - startY) > 0.0001f) { q.y = startY; model.localPosition = q; } return; }   // FootGround (IK) owns the feet: this incremental model-Y plant would fight it and sink the model (audit A)
        // plant the lower foot on the real ground (raycast under each toe), so feet land on the stair treads
        float g = transform.position.y, lowestRel = float.MaxValue;
        lowestRel = Mathf.Min(lowestRel, ToeRel(toeL, g));   // same order and maths as the old foreach over new[] { toeL, toeR } (no per-frame array)
        lowestRel = Mathf.Min(lowestRel, ToeRel(toeR, g));
        float d = Mathf.Clamp(-lowestRel, -0.25f, 0.25f);
        var p = model.localPosition; p.y += d * Mathf.Clamp01(Time.deltaTime * 5f); p.y = Mathf.Clamp(p.y, startY - maxModelDrop, startY + maxModelDrop); model.localPosition = p;   // never accumulate beyond +-5 cm of the authored model height
    }
    float ToeRel(Transform toe, float g)
    {
        float gy = g; if (Physics.Raycast(toe.position + Vector3.up * 0.6f, Vector3.down, out var hit, 2.5f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) gy = hit.point.y;
        return (toe.position.y - transform.position.y) - initialToeY - (gy - g);
    }
    float startY, initialToeY; bool startYSet, hasFootGround;
    public float toePlantY = -0.02f, maxModelDrop = 0.12f;   // toe-end bone height above the ground when planted (the sole lies ~6 cm below the Toe_End bone: verifyGround measured the enemy floating 5-8 cm with +0.02), and the max model-Y travel from the authored height
}






