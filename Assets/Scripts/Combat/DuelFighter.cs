using System.Collections.Generic;
using UnityEngine;

public enum FState { Loco, Windup, Active, Recovery, Guard, ParryRecover, Roll, Stagger, Down, Dead }

// One duelist. Combat state is advanced in whole 60 Hz ticks by Duel; movement and animation blending are per-frame presentation.
[RequireComponent(typeof(CharacterController), typeof(Health), typeof(Fighter))]
public class DuelFighter : MonoBehaviour
{
    public MoveTable moves; public RuleTable rules; public DuelFighter opponent;
    public float walkSpeed = 2.2f, runSpeed = 5.5f, maxHealth = 150f, maxStamina = 100f, radius = 0.4f;

    public FState state; public int t; public MoveDef move; bool chained; bool hitDone; int charge; int staggerTicks;
    public float health, stamina, posture;
    int regenWait, postureHold, guardAge, parryCd, riposteTicks, blockFlash; bool parryWindow;
    public Vector3 moveDir; public bool run, guardHeld, heavyHeld; public int guardSide;   // 0 centre, 1 left, 2 right, 3 up
    int pLight, pHeavy, pThrust, pKick, pRoll, pBack, pMoveTtl; string pMove; public int special1Cd = 120, special2Cd = 120;   // specials: first available at 2 s, 9 s cooldown, no stamina cost
    bool backstep; bool lastLightRight; Vector3 rollDir, knock; bool paused;
    // dodge.json: the current evasive move (state Roll covers roll, backstep and sidestep; dodgeKind tells them apart) and the moving-guard layer
    int pSide, sideDir, dodgeCd, dodgeKind, dodgeTicks = 36, dodgeIS = 4, dodgeIE = 20, dodgeCdTicks; float dodgeDist = 3.2f, dodgePow = 1f, dodgeAge, dodgeTurn = 30f;   // dodgeKind 0 roll, 1 backstep, 2 sidestep
    int guardLayer = -2; float guardLayerW; string guardLayerCur = "";
    public float scale = 1f, damageScale = 1f;
    Vector3 lastGroundedPos; bool hasGrounded;
    CharacterController cc; Health hp; Fighter f; float vy; Dictionary<string, float> clipLen;
    // attack clip plan (attack_clips.json): two-segment clip speed, contact on the move's hit tick; atkOn false = the old whole-clip squash
    bool atkOn, atkFresh, atkHold; int atkAge, atkHoldTick; float atkContactTick, atkSpA, atkSpB, atkSpC, atkFadeOut, fadeOutLen, fadeOutUntil; AttackClipTable.Row atkRow; string charKey;


    float R(string k, float d) { return rules ? rules.N(k, d) : d; }
    public bool Dead => state == FState.Dead;
    public MoveDef CurrentMove => move;
    public int WindupLeft => state == FState.Windup && move != null ? move.Windup(chained) - t : 99;

    // feint: cancel a wind-up before the move's feintUntil tick (V3: 10 stamina)
    public void Feint()
    {
        if (state != FState.Windup || move == null) return;
        if (t > 10 || stamina < R("feintCost", 10)) return;
        stamina -= R("feintCost", 10); regenWait = (int)R("regenDelay", 45); ToLoco(); PlayClip("idle", 0, 0.1f); f.holdLast = false;
    }
    public void Press(string what)
    {
        int ttl = (int)R("bufferTtl", 11);
        if (what == "special1" || what == "special2" || what == "light_left" || what == "light_right" || what == "heavy_overhead") { pMove = what == "special1" ? "skill_cleave" : what == "special2" ? "skill_reaping" : what; pMoveTtl = ttl; return; }
        switch (what) { case "sidestep": sideDir = 0; pSide = ttl; break; case "backstep": pBack = ttl; break; case "light": pLight = ttl; break; case "heavy": pHeavy = ttl; break; case "thrust": pThrust = ttl; break; case "kick": pKick = ttl; break; case "roll": pRoll = ttl; break; }
    }

    // sidestep request: dir -1 = left, +1 = right, 0 = back, relative to the opponent (dodge.json sidestep)
    public void PressSidestep(int dir) { sideDir = dir < 0 ? -1 : (dir > 0 ? 1 : 0); pSide = (int)R("bufferTtl", 11); }

    void Awake()
    {
        cc = GetComponent<CharacterController>(); hp = GetComponent<Health>(); f = GetComponent<Fighter>(); scale = transform.lossyScale.y;
        health = maxHealth; stamina = maxStamina; hp.max = maxHealth; hp.current = maxHealth;
    }
    void OnEnable() { Duel.Register(this); }
    void OnDisable() { Duel.Unregister(this); }

    float ClipLen(string n)
    {
        if (clipLen == null)
        {
            clipLen = new Dictionary<string, float>();
            foreach (var c in f.anim.runtimeAnimatorController.animationClips) clipLen[c.name] = c.length;
        }
        return clipLen.TryGetValue(n, out var l) ? l : 0f;
    }

    void PlayClip(string name, int totalTicks, float fade = 0.08f)
    {
        f.holdLast = false; paused = false;
        f.Force(name, fade);
        float len = ClipLen(name);
        f.anim.speed = (totalTicks > 0 && len > 0 ? len / (totalTicks / 60f) : 1f) * f.animWeight;
    }

    static string ClipFor(string id)
    {
        switch (id)
        {
            case "light_right": case "slash_riposte": return "light_attack_R";
            case "light_left": return "light_attack_L";
            case "thrust": return "thrust";
            case "skill_reaping": return "special_whirlwind";
            case "skill_cleave": return "special_skullcrack";
            case "riposte": return "riposte";
            case "kick": return "kick";
            default: return "heavy_release";
        }
    }

    string CharKey()
    {
        if (charKey == null) charKey = name.Contains("Pitborn") ? "Pitborn" : name.Contains("Dracula") ? "Dracula" : (transform.root.name.Contains("Pitborn") ? "Pitborn" : transform.root.name.Contains("Dracula") ? "Dracula" : "");
        return charKey;
    }
    bool HasState(string n) { return f != null && f.anim != null && !string.IsNullOrEmpty(n) && f.anim.HasState(0, Animator.StringToHash(n)); }

    // Plays the move's clip from attack_clips.json in two segments (wind-up up to the clip's contact time over the wind-up ticks, then contact..end over active + recovery),
    // so the blade connects on the tick Duel.Resolve runs. Returns false (nothing played) when there is no usable row, so the caller keeps the old squash.
    bool BeginAttackClip(AttackClipTable.Row row, MoveDef m, bool isChained)
    {
        atkOn = false; atkRow = null;
        if (row == null || f == null || f.anim == null || f.anim.runtimeAnimatorController == null) return false;
        var df = AttackClipTable.Get().defaults;
        string clip = row.clip; float st = row.startTime, ct = row.contactTime, en = row.endTime, hold = row.holdClipTime;
        if (!HasState(clip))
        {
            clip = row.fallbackClip; st = row.fbStartTime; ct = row.fbContactTime; en = row.fbEndTime; hold = row.fbHoldClipTime;
            if (!HasState(clip)) return false;
        }
        float len = ClipLen(clip);
        if (len <= 0.05f || ct <= 0.05f) return false;
        ct = Mathf.Min(ct, len); if (en <= ct + 0.05f || en > len) en = len;
        st = Mathf.Clamp(st, 0f, ct - 0.05f);
        int w = m.Windup(isChained), a = m.Active(isChained), r = m.Recovery(isChained), total = w + a + r;
        float contactTick = Mathf.Max(1f, w + df.contactActiveFrac * a);
        float windS = contactTick / 60f, recS = Mathf.Max(1f, total - contactTick) / 60f;
        int cham = (int)m.chamber;
        atkHold = m.charges && cham > 0 && cham < contactTick - 1f && hold > st + 0.02f && hold < ct - 0.02f;
        atkHoldTick = cham;
        if (atkHold)
        {
            atkSpA = Mathf.Min(df.maxWindupSpeed, (hold - st) / (cham / 60f));
            atkSpB = Mathf.Min(df.maxWindupSpeed, (ct - hold) / ((contactTick - cham) / 60f));
        }
        else
        {
            float sp;
            if (row.windupSpeed > 0f) sp = row.windupSpeed;
            else
            {
                sp = (ct - st) / windS;
                if (sp < df.minWindupSpeed) { sp = df.minWindupSpeed; st = Mathf.Max(0f, ct - sp * windS); }   // clip is too short for the wind-up: start later in it instead of slow motion
                sp = Mathf.Min(sp, df.maxWindupSpeed);
            }
            atkSpA = atkSpB = sp;
        }
        atkSpC = row.recoverySpeed > 0f ? row.recoverySpeed : Mathf.Min(df.maxRecoverySpeed, (en - ct) / recS);
        atkContactTick = contactTick; atkFadeOut = row.blendOut > 0f ? row.blendOut : df.blendOut; atkRow = row;
        f.holdLast = false; paused = false;
        f.ForceExact(clip, row.blendIn > 0f ? row.blendIn : df.blendIn, st);
        f.anim.speed = atkSpA;
        atkOn = true; atkAge = 0; atkFresh = true;
        if (AttackClipTable.Get().debugLog) Debug.Log("[AttackClip] " + name + " " + m.id + (isChained ? " (chain)" : "") + " -> " + clip + " start " + st.ToString("0.00") + " contact " + ct.ToString("0.00") + " end " + en.ToString("0.00") + " speeds " + atkSpA.ToString("0.00") + "/" + atkSpB.ToString("0.00") + "/" + atkSpC.ToString("0.00") + " contactTick " + contactTick.ToString("0.0") + " total " + total + (atkHold ? " hold@" + hold.ToString("0.00") + " tick " + cham : ""));
        return true;
    }

    // per sim tick: speed of the segment the next interval is in (the charge hold keeps the speed 0 written by TickWindup)
    void AtkSpeedTick()
    {
        if (!atkOn || paused || f == null || f.anim == null) return;
        float s = atkAge >= atkContactTick ? atkSpC : (atkHold && atkAge >= atkHoldTick ? atkSpB : atkSpA);
        f.anim.speed = s;
    }

    float StepInFor()
    {
        float s = move.stepIn;
        if (atkOn && atkRow != null) { if (atkRow.useStepIn) s = atkRow.stepIn; if (atkRow.maxStep > 0f) s = Mathf.Min(s, atkRow.maxStep); }
        return s;
    }
    float StepStopDist()
    {
        if (!atkOn) return 1.5f;
        float d = AttackClipTable.Get().defaults.stepStopDist;
        return d > 0f ? d : DodgeTable.Get().minSeparation + 0.05f;   // never lunge into the separation clamp (that pushed the opponent / skated)
    }

    // ---------------- tick (deterministic, 60 Hz) ----------------
    public void Tick()
    {
        if (state == FState.Dead) return;
        if (regenWait > 0) regenWait--; else stamina = Mathf.Min(maxStamina, stamina + R("regen", 0.667f));
        if (posture > 0) { if (postureHold > 0) postureHold--; else posture = Mathf.Max(0f, posture - R("posture.decay", 0.2f)); }
        if (parryCd > 0) parryCd--;
        if (riposteTicks > 0) riposteTicks--;
        if (blockFlash > 0) blockFlash--;
        if (special1Cd > 0) special1Cd--; if (special2Cd > 0) special2Cd--; if (pMoveTtl > 0) pMoveTtl--; else pMove = null; if (pBack > 0) pBack--;
        if (pLight > 0) pLight--; if (pHeavy > 0) pHeavy--; if (pThrust > 0) pThrust--; if (pKick > 0) pKick--; if (pRoll > 0) pRoll--;
        if (pSide > 0) pSide--; if (dodgeCd > 0 && state != FState.Roll) dodgeCd--;
        if (state == FState.Guard) { float gd = DodgeTable.Get().guard.staminaDrainPerSecond; if (gd > 0f) stamina = Mathf.Max(0f, stamina - gd / 60f); }   // data, OFF at 0
        t++;
        bool wasPaused = paused;
        switch (state)
        {
            case FState.Loco: case FState.Guard: TickFree(); break;
            case FState.Windup: TickWindup(); break;
            case FState.Active: TickActive(); break;
            case FState.Recovery: TickRecovery(); break;
            case FState.Roll: if (t >= dodgeTicks) { dodgeCd = dodgeCdTicks; ToLoco(); } break;
            case FState.ParryRecover: if (t >= R("parryRecovery", 8)) ToLoco(); break;
            case FState.Stagger: if (t >= staggerTicks) ToLoco(); break;
            case FState.Down:
                if (t == 54) PlayClip("get_up", 36);
                if (t >= R("posture.stun", 90)) ToLoco(); break;
        }
        if (atkOn && (state == FState.Windup || state == FState.Active || state == FState.Recovery))
        {
            if (atkFresh) atkFresh = false; else if (!(wasPaused && paused)) atkAge++;   // age = sim ticks played (stands still while the charge is parked)
            AtkSpeedTick();
        }
        hp.current = health;
    }

    void ToLoco()
    {
        if (atkOn && state == FState.Recovery) { fadeOutLen = atkFadeOut; fadeOutUntil = Time.time + 0.25f; }   // natural end of a move: blendOut into the next locomotion clip
        atkOn = false;
        state = FState.Loco; t = 0; f.holdLast = false; f.anim.speed = 1f;
    }

    void TickFree()
    {
        bool runCancel = run && moveDir.sqrMagnitude > 0.01f && DodgeTable.Get().enabled && DodgeTable.Get().guard.runDropsGuard;   // Shift + move while the guard key is held: the guard drops and he runs
        if (guardHeld && stamina > 0f && !runCancel)
        {
            if (state != FState.Guard)
            {
                state = FState.Guard; t = 0; guardAge = 0; parryWindow = parryCd == 0; parryCd = (int)R("parryCooldown", 30);
            }
            guardAge++;
        }
        else if (state == FState.Guard) { state = FState.Loco; t = 0; }

        if (pMove != null)
        {
            string id = pMove; pMove = null;
            if (id == "skill_cleave" && special1Cd > 0) { }
            else if (id == "skill_reaping" && special2Cd > 0) { }
            else { if (id == "skill_cleave") special1Cd = 540; else if (id == "skill_reaping") special2Cd = 540; stamina += moves.Get(id) != null ? moves.Get(id).stamina : 0; StartMove(id, false); return; }
        }
        if (dodgeCd <= 0)
        {
            if (pBack > 0 && stamina >= BackstepCost()) { StartBackstep(); return; }
            if (pSide > 0 && stamina >= SidestepCost()) { StartSidestep(); return; }
            if (pRoll > 0 && stamina >= RollCost()) { StartRoll(); return; }
        }
        if (riposteTicks > 0 && (pLight > 0 || pHeavy > 0 || pThrust > 0)) { StartMove("riposte", false); return; }
        if (pHeavy > 0) { StartMove("heavy_overhead", false); return; }
        if (pLight > 0) { StartMove(lastLightRight ? "light_left" : "light_right", false); return; }
        if (pThrust > 0) { StartMove("thrust", false); return; }
        if (pKick > 0) { StartMove("kick", false); return; }
    }

    // ---------------- dodges (dodge.json; "enabled": false falls back to the V3 rules-defs values) ----------------
    float RollCost() { var d = DodgeTable.Get(); return d.enabled ? d.roll.stamina : R("rollCost", 30f); }
    float BackstepCost() { var d = DodgeTable.Get(); return d.enabled ? d.backstep.stamina : R("backstep.cost", 10f); }
    float SidestepCost() { return DodgeTable.Get().sidestep.stamina; }

    Vector3 OppFlat()   // unit ground direction to the opponent (own forward when there is none)
    {
        Vector3 o = (opponent && !opponent.Dead) ? opponent.transform.position - transform.position : transform.forward; o.y = 0f;
        if (o.sqrMagnitude < 1e-4f) { o = transform.forward; o.y = 0f; }
        if (o.sqrMagnitude < 1e-4f) o = Vector3.forward;
        return o.normalized;
    }

    // kind: 0 roll, 1 backstep, 2 sidestep. All three run in FState.Roll so the AI, Duel.Resolve (IFrames) and the camera/FX hooks keep working.
    void BeginDodge(int kind, int ticks, float dist, int isf, int ief, float pow, int cd, float turn)
    {
        dodgeKind = kind; backstep = kind == 1; dodgeTicks = Mathf.Max(1, ticks); dodgeDist = dist; dodgeIS = isf; dodgeIE = ief;
        dodgePow = Mathf.Max(1f, pow); dodgeCdTicks = Mathf.Max(0, cd); dodgeTurn = turn; dodgeAge = 0f;
        state = FState.Roll; t = 0; pRoll = 0; pBack = 0; pSide = 0; atkOn = false; fadeOutLen = 0f;
    }

    void PlayDodgeClip(string clip, DodgeTable.DodgeRow r)
    {
        if (r.clipSpeed > 0f) { PlayClip(clip, 0); f.anim.speed = r.clipSpeed * f.animWeight; }
        else PlayClip(clip, r.ticks + r.clipTailTicks);
    }

    void StartRoll()
    {
        var d = DodgeTable.Get(); var r = d.roll;
        int ticks = (int)R("roll", 36), isf = (int)R("safeStart", 4), ief = (int)R("safeEnd", 20); float dist = 3.2f, cost = R("rollCost", 30f), pow = 1f, turn = 12f; int cd = 0;
        if (d.enabled) { ticks = r.ticks; isf = r.iframeStart; ief = r.iframeEnd; dist = r.distance; cost = r.stamina; pow = r.easePower; turn = r.turnRate; cd = r.cooldownTicks; }
        stamina -= cost; regenWait = (int)R("regenDelay", 45);
        if (moveDir.sqrMagnitude > 0.01f) rollDir = moveDir.normalized;
        else
        {
            string nd = d.enabled ? r.noInputDir : "back";
            Vector3 fw = transform.forward; fw.y = 0f; if (fw.sqrMagnitude < 1e-4f) fw = Vector3.forward;
            rollDir = nd == "forward" ? fw.normalized : (nd == "away" ? -OppFlat() : -fw.normalized);
        }
        rollDir.y = 0f;
        BeginDodge(0, ticks, dist, isf, ief, pow, cd, turn);
        string clip = (d.enabled && !string.IsNullOrEmpty(r.clip)) ? r.clip : "roll_forward";
        if (!HasState(clip)) { clip = "roll_forward"; if (d.enabled) { var o = new DodgeTable.DodgeRow { ticks = ticks, clipTailTicks = 0 }; PlayDodgeClip(clip, o); } else PlayClip(clip, ticks); }   // character without the baked dodge clip (Pitborn): old roll clip
        else if (d.enabled) PlayDodgeClip(clip, r); else PlayClip(clip, ticks);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " ROLL dir=" + rollDir + " ticks=" + ticks + " dist=" + dist + " stamina=" + stamina);
        ContactRelay.Action(this, (d.enabled && !string.IsNullOrEmpty(r.fxAction)) ? r.fxAction : "roll");
        ContactRelay.Dodge(this, "roll", rollDir);
    }

    void StartBackstep()
    {
        var d = DodgeTable.Get(); var r = d.backstep;
        int ticks = (int)R("backstep.ticks", 12), isf = (int)R("safeStart", 4), ief = (int)R("safeEnd", 20); float dist = 1.4f, cost = R("backstep.cost", 10f), pow = 1f; int cd = 0;
        if (d.enabled) { ticks = r.ticks; isf = r.iframeStart; ief = r.iframeEnd; dist = r.distance; cost = r.stamina; pow = r.easePower; cd = r.cooldownTicks; }
        stamina -= cost; regenWait = (int)R("regenDelay", 45);
        rollDir = -OppFlat();   // straight away from the opponent (the old -transform.forward is the same while he is faced); the body keeps facing him
        BeginDodge(1, ticks, dist, isf, ief, pow, cd, 12f);
        string clip = (d.enabled && !string.IsNullOrEmpty(r.clip)) ? r.clip : "backstep";
        if (d.enabled) PlayDodgeClip(clip, r); else PlayClip(clip, ticks + 30);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " BACKSTEP ticks=" + ticks + " stamina=" + stamina);
        ContactRelay.Action(this, (d.enabled && !string.IsNullOrEmpty(r.fxAction)) ? r.fxAction : "backstep");
        ContactRelay.Dodge(this, "backstep", rollDir);
    }

    void StartSidestep()
    {
        var d = DodgeTable.Get(); var r = d.sidestep;
        if (!d.enabled) { pSide = 0; return; }
        Vector3 toO = OppFlat(); Vector3 right = Vector3.Cross(Vector3.up, toO);   // right of the line to the opponent
        string clip;
        if (sideDir < 0) { rollDir = -right; clip = r.clipLeft; }
        else if (sideDir > 0) { rollDir = right; clip = r.clipRight; }
        else { rollDir = -toO; clip = r.clipBack; }
        if (string.IsNullOrEmpty(clip)) clip = r.clip;
        stamina -= r.stamina; regenWait = (int)R("regenDelay", 45);
        BeginDodge(2, r.ticks, r.distance, r.iframeStart, r.iframeEnd, r.easePower, r.cooldownTicks, 12f);
        PlayDodgeClip(clip, r);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " SIDESTEP dir=" + sideDir + " clip=" + clip + " stamina=" + stamina);
        ContactRelay.Action(this, string.IsNullOrEmpty(r.fxAction) ? "backstep" : r.fxAction);
        ContactRelay.Dodge(this, "sidestep", rollDir);
    }

    void StartMove(string id, bool isChained)
    {
        var m = moves.Get(id); if (m == null) return;
        move = m; chained = isChained; state = FState.Windup; t = 0; hitDone = false; charge = 0;
        pLight = pHeavy = pThrust = pKick = 0;
        if (id == "light_right") lastLightRight = true; else if (id == "light_left") lastLightRight = false;
        stamina = Mathf.Max(0f, stamina - m.stamina); regenWait = (int)R("regenDelay", 45);
        int total = m.Windup(isChained) + m.Active(isChained) + m.Recovery(isChained);
        var arow = AttackClipTable.Get().enabled ? AttackClipTable.Find(CharKey(), id) : null;
        fadeOutLen = 0f;
        if (!BeginAttackClip(arow, m, isChained)) PlayClip(ClipFor(id), total, 0.06f);   // no row / state: old whole-clip squash
        ContactRelay.AttackStarted(this, m);
    }

    void TickWindup()
    {
        int w = move.Windup(chained);
        if (move.charges && t >= (int)move.chamber && heavyHeld && charge < (int)R("charge.max", 54))
        {
            charge++; t = (int)move.chamber; if (!paused) { paused = true; f.anim.speed = 0f; }
            return;
        }
        if (paused) { paused = false; if (!atkOn) f.anim.speed = ClipLen("heavy_release") / ((w + move.Active(chained) + move.Recovery(chained)) / 60f); }
        if (t >= w) { state = FState.Active; t = 0; }
    }

    void TickActive()
    {
        if (!hitDone && opponent != null)
        {
            float mul = (move.charges && charge >= (int)R("charge.min", 30)) ? R("charge.damage", 1.5f) : 1f;
            if (Duel.Resolve(this, opponent, move, mul)) hitDone = true;
        }
        if (t >= move.Active(chained)) { if (!hitDone) ContactRelay.AttackMissed(this, move); state = FState.Recovery; t = 0; }
    }

    void TickRecovery()
    {
        if (move.chainFollow != null && t <= move.chainWindow)
        {
            if (pHeavy > 0 && System.Array.IndexOf(move.chainFollow, "heavy_overhead") >= 0) { StartMove("heavy_overhead", true); return; }
            if (pLight > 0) { foreach (var fid in move.chainFollow) if (fid.StartsWith("light")) { StartMove(fid, true); return; } }
        }
        if (dodgeCd <= 0 && t > move.Recovery(chained) / 2)
        {
            if (pSide > 0 && stamina >= SidestepCost()) { StartSidestep(); return; }
            if (pRoll > 0 && stamina >= RollCost()) { StartRoll(); return; }
        }
        if (t >= move.Recovery(chained)) ToLoco();
    }

    // ---------------- reactions (called by Duel.Resolve) ----------------
    public void Stagger(int ticks, string clip)
    {
        if (state == FState.Dead) return;
        atkOn = false; fadeOutLen = 0f; state = FState.Stagger; t = 0; staggerTicks = Mathf.Max(8, ticks); pLight = pHeavy = pThrust = pKick = 0;
        PlayClip(clip, 0, 0.05f);
    }
    public void KnockDown()
    {
        if (state == FState.Dead) return;
        atkOn = false; fadeOutLen = 0f; state = FState.Down; t = 0; posture = 0f; PlayClip("knockdown", 54, 0.05f);
    }
    public void Die()
    {
        atkOn = false; fadeOutLen = 0f; state = FState.Dead; t = 0; health = 0f; hp.current = 0f; ContactRelay.Killed(this, move);
        PlayClip("death", 0, 0.08f); f.holdLast = true;
        var col = GetComponent<Collider>(); if (col) col.enabled = false;
        hp.onDied.Invoke();
    }
    public void ParrySuccess() { atkOn = false; fadeOutLen = 0f; state = FState.ParryRecover; t = 0; riposteTicks = (int)R("parryStun", 90); PlayClip("parry", (int)R("parryRecovery", 8) + 10, 0.04f); }
    public void BlockHit(bool heavy) { blockFlash = 10; f.Force("guard_hit", 0.04f); f.anim.speed = 1.4f; }
    public void AddPosture(float p) { posture += p; postureHold = (int)R("posture.hold", 60); }
    public void SetKnock(Vector3 v) { knock = v; }
    public bool InParryWindow => state == FState.Guard && parryWindow && guardAge <= (int)R("parry", 10);
    public bool IsGuarding => state == FState.Guard;
    public bool IFrames => state == FState.Roll && t >= dodgeIS && t <= dodgeIE;   // set per move by BeginDodge (dodge.json iframeStart/End)
    public bool InWindup => state == FState.Windup;
    public float Rule(string k, float d) { return R(k, d); }
    public void SpendStamina(float s) { stamina -= s; regenWait = (int)R("regenDelay", 45); }

    // ---------------- presentation (per frame) ----------------
    void Update()
    {
        float dt = Time.deltaTime;
        Vector3 v = Vector3.zero;
        Vector3 toOpp = opponent ? opponent.transform.position - transform.position : transform.forward; toOpp.y = 0f;
        float dist = toOpp.magnitude; Vector3 dir = dist > 0.01f ? toOpp / dist : transform.forward;
        bool faceOpp = opponent && !opponent.Dead && dist < 14f;
        switch (state)
        {
            case FState.Loco: v = moveDir * (run ? runSpeed : walkSpeed); break;
            case FState.Guard: { var dg = DodgeTable.Get(); v = moveDir * walkSpeed * (dg.enabled ? dg.guard.speedMul : R("guardSpeed", 0.35f) * 1.6f); break; }   // run is off while guarding (the branch never reads `run`)
            case FState.Roll:
            {
                // distance travelled follows 1-(1-x)^power over the move (power 1 = constant speed); sim time, so hit-stop freezes it
                float durS = dodgeTicks / 60f, a0 = Mathf.Clamp01(dodgeAge / durS);
                dodgeAge += PresentationClock.SimDelta(dt);
                float a1 = Mathf.Clamp01(dodgeAge / durS);
                float step = (Mathf.Pow(1f - a0, dodgePow) - Mathf.Pow(1f - a1, dodgePow)) * dodgeDist * scale;
                v = rollDir * (step / Mathf.Max(dt, 1e-4f));
                if (dodgeKind == 0) faceOpp = false;   // a roll turns to its direction; backstep and sidestep keep facing the opponent
                break;
            }
            case FState.Windup:
                if (t >= R("stepInFrom", 3) && move != null && dist > StepStopDist()) { float si = StepInFor(); if (si > 0f) v = dir * (si * scale / Mathf.Max(0.1f, (move.Windup(chained) - 3) / 60f)); }   // stepIn: data (attack_clips.json) or the move's value
                break;
            case FState.Stagger: case FState.Down: v = knock; knock *= Mathf.Pow(0.88f, dt * 60f); faceOpp = false; break;
            case FState.Dead: faceOpp = false; break;
        }
        vy = cc.isGrounded ? -1f : vy - 20f * dt;
        v.y = vy; if (cc.enabled) cc.Move(v * dt);
        // kill-plane recovery (audit: a knockback pushed a fighter off the ground collider and it fell for ever, the camera followed): remember the last grounded position, restore it when far below
        if (cc.enabled && cc.isGrounded && transform.position.y > -0.5f) { lastGroundedPos = transform.position; hasGrounded = true; }
        if (hasGrounded && transform.position.y < lastGroundedPos.y - 2f)
        {
            cc.enabled = false; transform.position = lastGroundedPos + Vector3.up * 0.05f; cc.enabled = true; Physics.SyncTransforms(); vy = -1f; knock = Vector3.zero;
            Debug.Log("[DuelFighter] " + name + " fell out of the world: restored to " + lastGroundedPos);
        }
        float lim = 2.45f;   // keep both fighters inside the stair width (no climbing onto the balustrade)
        if (cc.enabled && transform.position.z > 4f && transform.position.z < 22.5f && Mathf.Abs(transform.position.x) > lim) cc.Move(new Vector3(Mathf.Sign(transform.position.x) * lim - transform.position.x, 0f, 0f));

        // never overlap the opponent: keep a minimum horizontal separation (radii + 0.25 m), each fighter yields half of the overlap, in every state
        if (cc.enabled && opponent && state != FState.Dead && !opponent.Dead)
        {
            Vector3 oq = opponent.transform.position - transform.position; oq.y = 0f; float dq = oq.magnitude, minSep = Mathf.Max(DodgeTable.Get().minSeparation, radius + opponent.radius + DodgeTable.Get().separationPad);
            if (dq < minSep) cc.Move((dq > 0.001f ? -oq / dq : -transform.forward) * ((minSep - dq) * 0.5f));
        }
        if (state == FState.Roll && dodgeKind == 0 && rollDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(rollDir), Mathf.Min(1f, dodgeTurn * dt));
        else if (faceOpp && state != FState.Stagger && state != FState.Down) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 10f * dt);
        else if (state == FState.Loco && moveDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), 10f * dt);

        string upperPose = null;   // guard pose for the masked upper-body layer; null = layer off
        if (state == FState.Loco || (state == FState.Guard && blockFlash == 0))
        {
            f.anim.speed = f.animWeight;
            bool moving = moveDir.sqrMagnitude > 0.01f;
            bool guarding = state == FState.Guard;
            bool runNow = run && !guarding;   // no running while the guard is up
            string clip;
            string pose = guardSide == 1 ? "guard_left" : guardSide == 2 ? "guard_right" : guardSide == 3 ? "guard_up" : "guard_idle";
            if (!moving) clip = guarding ? "guard_idle" : "idle";
            else
            {
                Vector3 lm = transform.InverseTransformDirection(moveDir);
                bool strafe = faceOpp && Mathf.Abs(lm.x) > Mathf.Abs(lm.z) * 0.9f && !runNow;
                clip = strafe ? (lm.x < 0 ? "strafe_L" : "strafe_R") : (runNow ? "run" : "walk");
            }
            if (guarding && !moving) clip = pose;
            if (guarding && moving)
            {
                f.anim.speed = f.animWeight * Mathf.Max(0.05f, DodgeTable.Get().guard.legAnimSpeed);   // legs step at the guard-walk pace (data), not the full walk cadence
                if (GuardLayerReady()) upperPose = pose;                    // legs = walk/strafe on layer 0, spine + arms = guard pose on the layer
                else if (DodgeTable.Get().guard.moveClipFallback != "locomotion") clip = pose;   // no layer built yet: whole-body guard pose while moving
            }
            if (fadeOutLen > 0f)
            {
                if (Time.time > fadeOutUntil) fadeOutLen = 0f;
                else if (f.Current != clip) { f.PlayExact(clip, fadeOutLen); fadeOutLen = 0f; }   // first locomotion clip after an attack: the row's blendOut
            }
            else f.Play(clip, 0.15f);
        }
        UpdateGuardLayer(upperPose, dt);
    }

    // ---------------- moving guard: masked upper-body Animator layer (Assets/Editor/GuardLayerSetup.cs builds it) ----------------
    bool GuardLayerReady()
    {
        if (guardLayer == -2)
        {
            var g = DodgeTable.Get().guard; guardLayer = -1;
            if (g.upperBodyLayer && !string.IsNullOrEmpty(g.layer) && f != null && f.anim != null) guardLayer = f.anim.GetLayerIndex(g.layer);
        }
        return guardLayer > 0;
    }

    void UpdateGuardLayer(string pose, float dt)
    {
        if (!GuardLayerReady()) return;
        var g = DodgeTable.Get().guard;
        if (pose == null)
        {
            if (guardLayerW > 0f) { guardLayerW = 0f; f.anim.SetLayerWeight(guardLayer, 0f); }   // any other state: layer off at once so it never overrides a swing / hit clip
            guardLayerCur = "";
            return;
        }
        if (guardLayerCur != pose) { guardLayerCur = pose; f.anim.CrossFadeInFixedTime(pose, g.poseFade, guardLayer, 0f); }
        guardLayerW = Mathf.MoveTowards(guardLayerW, 1f, dt / Mathf.Max(0.01f, g.layerBlendIn));
        f.anim.SetLayerWeight(guardLayer, guardLayerW);
    }
}












