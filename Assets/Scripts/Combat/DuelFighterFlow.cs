using UnityEngine;

// Moveset FLOW for the human-driven fighter (lane V64FLOW): chained combos with data cancel windows, lock-on facing, attack magnetism + limited turn speed,
// directional dodge overrides, dedicated riposte key. All numbers: Assets/Data/flow.json via FlowTable. Every entry point returns at once unless FlowTable.Applies(this).
// Hooks into DuelFighter (apply_v64flow.py): Tick -> FlowTickPre, StartMove -> FlowAttackStart, TickRecovery -> FlowChain / FlowChainOwns, TickFree -> FlowRiposteFree / FlowRiposteAuto,
// BeginDodge -> FlowDodgeBegin, Roll/Backstep/Sidestep cost -> FlowDodgeCost, StartRoll -> FlowRollAxis, Update -> FlowFace / FlowLockOk / FlowFaceRange / FlowStepDir.
public partial class DuelFighter
{
    public bool lockFacing = true;   // true = always face the opponent (V3 and the AI); the human's PlayerBrain sets it from the lock-on toggle (CameraRig.lockEnabled)

    PlayerBrain flowPb; bool flowPbSet;
    // true when a human drives this fighter (PlayerBrain enabled) or a proof harness forces it
    public bool FlowHuman
    {
        get
        {
            if (FlowTable.ForceApply) return IsPlayer;
            if (!flowPbSet) { flowPbSet = true; flowPb = GetComponent<PlayerBrain>(); }
            return flowPb != null && flowPb.enabled;
        }
    }

    // ---- diagnostics for the proof harness / balance logs ----
    public int flowChains, flowRipostes; public string flowLastChain = ""; public float flowMaxTurnWindup, flowMaxTurnActive, flowMaxTurnRecovery; public bool flowMagnetUsed; public string flowDodgeDir = "";
    public int DodgeIFrameStart => dodgeIS; public int DodgeIFrameEnd => dodgeIE; public float DodgeDistance => dodgeDist; public int DodgeKind => dodgeKind; public int RiposteLeft => riposteTicks; public bool HitDone => hitDone;
    public int MoveT => t;
    public void FlowResetTurnStats() { flowMaxTurnWindup = flowMaxTurnActive = flowMaxTurnRecovery = 0f; }

    int pRipo, flowLinks; string pendDir = "";
    float magnetLeft, magnetTotal; Vector3 attackDir; bool attackDirSet, magnetEligible;

    // ---------------- inputs ----------------
    // dedicated riposte key: only does something while the parry riposte window is open (riposteTicks); the press waits out the 8-tick parry recovery
    public void PressRiposte()
    {
        if (state == FState.Dead || riposteTicks <= 0 || !FlowTable.Applies(this) || !FlowTable.Get().riposte.enabled) return;
        pRipo = Mathf.Max(2, CombatFeel.QueueTtl(this, (int)R("bufferTtl", 11)));
    }
    // the direction label of the next dodge (forward | back | left | right | free), set by PlayerBrain right before Press / PressSidestep; consumed by the dodge that starts
    public void SetDodgeDir(string d) { pendDir = d ?? ""; }

    void FlowTickPre()
    {
        if (pRipo > 0) pRipo--;
        if (pendDir.Length > 0 && pRoll <= 0 && pBack <= 0 && pSide <= 0) pendDir = "";
        if (state != FState.Windup && state != FState.Active && state != FState.Recovery) flowLinks = 0;
    }

    // ---------------- chain: attack-to-attack ----------------
    bool FlowChainOwns() { return FlowTable.Applies(this) && FlowTable.Get().chain.replaceBuiltin; }

    // Recovery tick: a queued attack runs at the first tick a flow.json chain row allows. Dodge presses win (TryCancel handles them); specials ride the cancel table.
    bool FlowChain()
    {
        if (!FlowTable.Applies(this) || move == null) return false;
        var C = FlowTable.Get().chain; if (C.rows.Length == 0) return false;
        if (dodgeCd <= 0 && (pRoll > 0 || pSide > 0 || pBack > 0)) return false;
        if (pMove != null) return false;
        if (C.maxLinks > 0 && flowLinks >= C.maxLinks) return false;
        for (int k = 0; k < 4; k++)
        {
            string type = k == 0 ? "heavy" : k == 1 ? "light" : k == 2 ? "thrust" : "kick";
            bool pressed = k == 0 ? pHeavy > 0 : k == 1 ? pLight > 0 : k == 2 ? pThrust > 0 : pKick > 0;
            if (!pressed) continue;
            var row = FlowTable.FindChain(move.id, type, t, hitDone); if (row == null) continue;
            if (type == "heavy" && !HeavyAllowed()) { pLight = Mathf.Max(pLight, pHeavy); pHeavy = 0; continue; }   // same downgrade as TickFree
            string id = type == "heavy" ? "heavy_overhead" : type == "light" ? (lastLightRight ? "light_left" : "light_right") : type;
            if (moves.Get(id) == null) continue;
            flowLinks++; flowChains++; flowLastChain = move.id + ">" + id;
            if (FlowTable.Get().debugLog) Debug.Log("[Flow] " + name + " CHAIN " + flowLastChain + " at recovery t=" + t + " (link " + flowLinks + ", hit " + hitDone + ")");
            StartMove(id, row.chained);
            return true;
        }
        return false;
    }

    // ---------------- riposte ----------------
    bool FlowRiposteFree()
    {
        if (pRipo <= 0 || riposteTicks <= 0) return false;
        pRipo = 0;
        if (!FlowTable.Applies(this)) return false;
        var Rp = FlowTable.Get().riposte; if (!Rp.enabled) return false;
        string mv = !string.IsNullOrEmpty(Rp.move) && moves.Get(Rp.move) != null ? Rp.move : "riposte";
        if (Rp.consume) riposteTicks = 0;
        flowRipostes++;
        StartMove(mv, false);
        return true;
    }
    bool FlowRiposteAuto() { return !FlowTable.Applies(this) || FlowTable.Get().riposte.auto; }

    // ---------------- directional dodge ----------------
    float FlowDodgeCost(float baseCost)
    {
        if (pendDir.Length == 0 || !FlowTable.Applies(this)) return baseCost;
        var r = FlowTable.DodgeDir(pendDir); return r != null && r.stamina >= 0f ? r.stamina : baseCost;
    }
    void FlowDodgeBegin(int kind)
    {
        string dir = pendDir; pendDir = ""; flowDodgeDir = dir;
        if (dir.Length == 0 || !FlowTable.Applies(this)) return;
        var r = FlowTable.DodgeDir(dir); if (r == null) return;
        if (r.iframeStart >= 0) dodgeIS = r.iframeStart;
        if (r.iframeEnd >= 0) dodgeIE = r.iframeEnd;
        if (r.distance >= 0f) dodgeDist = r.distance;
        if (FlowTable.Get().debugLog) Debug.Log("[Flow] " + name + " DODGE " + dir + " kind=" + kind + " iframes " + dodgeIS + ".." + dodgeIE + " dist " + dodgeDist + " stamina " + stamina);
    }
    void FlowRollAxis()
    {
        var c = FlowTable.Get().dodge;
        if (pendDir == "forward" && lockFacing && c.snapToAxes && FlowTable.Applies(this)) rollDir = OppFlat();   // forward = exactly at the target
    }

    // ---------------- facing: lock-on, magnetism, limited turn speed ----------------
    bool FlowLockOk()
    {
        if (!FlowTable.Applies(this) || !FlowTable.Get().lockOn.enabled) return true;
        return lockFacing || state != FState.Loco && (state != FState.Guard || FlowTable.Get().lockOn.guardFacesTarget);   // lock off: free locomotion (faces the move direction), guard still faces the target
    }
    float FlowFaceRange(float d) { return FlowTable.Applies(this) ? FlowTable.Get().lockOn.range : d; }
    Vector3 FlowStepDir(Vector3 dir)
    {
        if (!FlowTable.Applies(this) || lockFacing || !attackDirSet) return dir;
        return magnetEligible ? dir : attackDir;   // lock off and nothing to snap to: the wind-up step goes where the swing points
    }

    // StartMove: decide what this swing turns toward. Locked: the live target direction. Unlocked: the held stick direction (else the current facing); if the target is inside the
    // magnetism cone / range around that, snap to the target instead. The snap takes magnet.snapMs; beyond it the per-phase turn cap applies.
    void FlowAttackStart(MoveDef m)
    {
        magnetLeft = 0f; magnetTotal = 0f; attackDirSet = false; magnetEligible = false; flowMagnetUsed = false;
        if (m == null || !FlowTable.Applies(this) || !FlowTable.Get().attack.turn.enabled) return;
        var A = FlowTable.Attack(m.id);
        bool haveOpp = opponent != null && !opponent.Dead;
        Vector3 toO = OppFlat();
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude < 1e-4f ? toO : fwd.normalized;
        Vector3 intent = lockFacing && haveOpp ? toO : (moveDir.sqrMagnitude > 0.01f ? new Vector3(moveDir.x, 0f, moveDir.z).normalized : fwd);
        float dist = 999f, ang = 999f;
        if (haveOpp) { Vector3 o = opponent.transform.position - transform.position; o.y = 0f; dist = o.magnitude; ang = Vector3.Angle(intent, toO); }
        magnetEligible = A.magnet && haveOpp && dist <= A.maxDist * scale + 0.001f && ang <= A.maxAngle;
        attackDir = magnetEligible ? toO : intent; attackDirSet = true;
        if (magnetEligible) { magnetTotal = Mathf.Max(0.001f, A.snapMs / 1000f); magnetLeft = magnetTotal; flowMagnetUsed = true; }
        if (FlowTable.Get().debugLog) Debug.Log("[Flow] " + name + " ATTACK " + m.id + " lock=" + lockFacing + " dist=" + dist.ToString("0.00") + " angle=" + ang.ToString("0") + " magnet=" + magnetEligible);
    }

    // Update: rotation during Windup / Active / Recovery of a human swing. Returns true when it handled the rotation this frame.
    bool FlowFace(float dt, Vector3 dirToOpp, float dist, bool faceOpp)
    {
        if (state != FState.Windup && state != FState.Active && state != FState.Recovery) return false;
        if (move == null || !FlowTable.Applies(this) || !FlowTable.Get().attack.turn.enabled) return false;
        if (PresentationClock.Frozen) return true;   // hit-stop: the body stands still
        var A = FlowTable.Attack(move.id);
        bool haveOpp = opponent != null && !opponent.Dead && dist < FlowTable.Get().lockOn.range;
        Vector3 want = lockFacing && haveOpp ? dirToOpp : (attackDirSet ? attackDir : transform.forward);
        want.y = 0f; if (want.sqrMagnitude < 1e-4f) return true;
        Quaternion tgt = Quaternion.LookRotation(want.normalized);
        float cap = state == FState.Windup ? A.windup : state == FState.Active ? A.active : A.recovery;
        float step = cap * dt;
        float ang = Quaternion.Angle(transform.rotation, tgt);
        if (magnetLeft > 0f)
        {
            float rem = Mathf.Max(magnetLeft, dt);
            step = Mathf.Max(step, ang * Mathf.Min(1f, dt / rem));   // snap: close the whole remaining angle by the end of snapMs
            magnetLeft -= dt;
        }
        Quaternion before = transform.rotation;
        transform.rotation = Quaternion.RotateTowards(before, tgt, step);
        float rate = dt > 1e-5f ? Quaternion.Angle(before, transform.rotation) / dt : 0f;
        if (state == FState.Windup) flowMaxTurnWindup = Mathf.Max(flowMaxTurnWindup, rate); else if (state == FState.Active) flowMaxTurnActive = Mathf.Max(flowMaxTurnActive, rate); else flowMaxTurnRecovery = Mathf.Max(flowMaxTurnRecovery, rate);
        return true;
    }
}
