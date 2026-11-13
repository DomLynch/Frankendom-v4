using System.Collections.Generic;
using UnityEngine;

public enum FState { Loco, Windup, Active, Recovery, Guard, ParryRecover, Roll, Stagger, Down, Dead }

// One duelist. Combat state is advanced in whole 60 Hz ticks by Duel; movement and animation blending are per-frame presentation.
[RequireComponent(typeof(CharacterController), typeof(Health), typeof(Fighter))]
public partial class DuelFighter : MonoBehaviour   // V64FLOW: partial, moveset flow in DuelFighterFlow.cs
{
    public MoveTable moves; public RuleTable rules; public DuelFighter opponent;
    public float walkSpeed = 2.2f, runSpeed = 5.5f, maxHealth = 150f, maxStamina = 100f, radius = 0.4f;

    public FState state; public int t; public MoveDef move; bool chained; bool hitDone; int charge; int staggerTicks;
    public float health, stamina, posture;
    int regenWait, postureHold, guardAge, parryCd, riposteTicks, blockFlash; bool parryWindow;
    public Vector3 moveDir; public bool run, guardHeld, heavyHeld; public int guardSide;   // 0 centre, 1 left, 2 right, 3 up
    int pLight, pHeavy, pThrust, pKick, pRoll, pBack, pMoveTtl; string pMove; public int special1Cd = 120, special2Cd = 120;   // specials: first available at 2 s, 9 s cooldown, no stamina cost
    public int special3Cd, special4Cd, evadeLeft;   // ABILITIES v1 (AbilityTable.cs): slot 3 (shield bash) cooldown, slot 4 spare, ticks left of the dodge-strike window (0 = closed)
    bool backstep; bool lastLightRight; Vector3 rollDir, knock; bool paused;
    // dodge.json: the current evasive move (state Roll covers roll, backstep and sidestep; dodgeKind tells them apart) and the moving-guard layer
    int pSide, sideDir, dodgeCd, dodgeKind, dodgeTicks = 36, dodgeIS = 4, dodgeIE = 20, dodgeCdTicks; float dodgeDist = 3.2f, dodgePow = 1f, dodgeAge, dodgeTurn = 30f;   // dodgeKind 0 roll, 1 backstep, 2 sidestep
    int guardLayer = -2; float guardLayerW; string guardLayerCur = "";
    public float scale = 1f, damageScale = 1f;
    Vector3 lastGroundedPos; bool hasGrounded; int restoreCount; float restoreWinStart;   // ROBUST v1
    CharacterController cc; Health hp; Fighter f; float vy; Dictionary<string, float> clipLen;
    // attack clip plan (attack_clips.json): two-segment clip speed, contact on the move's hit tick; atkOn false = the old whole-clip squash
    bool atkOn, atkFresh, atkHold; int atkAge, atkHoldTick; float atkContactTick, atkSpA, atkSpB, atkSpC, atkFadeOut, fadeOutLen, fadeOutUntil; AttackClipTable.Row atkRow; string charKey;


    float R(string k, float d) { return rules ? rules.N(k, d) : d; }
    string[] specialIds; /* DATALAYER b6 */
    public string SpecialId(int slot) { string bowId = BowHooks.SpecialIdFor(this, slot); if (bowId != null) return bowId; /* BOW v2 */ var abr = AbilityTable.For(this, slot); if (abr != null) return abr.moveId; /* ABILITIES v1: weapon-aware slot row */ if (slot > 1) return null; if (specialIds == null) { var rc = Roster.Get(Roster.IdOf(this)); specialIds = rc != null && rc.specials != null && rc.specials.Length >= 2 ? rc.specials : new[] { "skill_cleave", "skill_reaping" }; } return specialIds[slot]; } /* DATALAYER b6 */
    // ABILITIES v1: slot cooldowns 0..3 (special1Cd / special2Cd stay the fields the AI, FightBalance, STTactics and FinisherPlayer read), readiness, price check. All false / unchanged when AbilityTable is off.
    public int SlotCd(int i) { return i == 0 ? special1Cd : i == 1 ? special2Cd : i == 2 ? special3Cd : special4Cd; }
    public bool SlotReady(int i) { var r = AbilityTable.For(this, i); return r != null && SlotCd(i) <= 0 && !AbilityPoor(r.moveId); }
    bool AbilityPoor(string id) { float c = AbilityTable.CostOf(this, id, -1f); return c > 0f && stamina < c * StanceLayer.AttackCostMul(this); }
    public bool Dead => state == FState.Dead;
    public MoveDef CurrentMove => move;
    public int WindupLeft => state == FState.Windup && move != null ? Tk(move.Windup(chained)) - t : 99;

    // feint: cancel a wind-up before the move's feintUntil tick (V3: 10 stamina)
    public void Feint()
    {
        if (state != FState.Windup || move == null) return;
        float fCost = R("feintCost", 10) * StanceLayer.FeintCostMul(this);   // lanePARRY: stance feint cost (stances.json)
        if (t > 10 || stamina < fCost) return;
        stamina -= fCost; regenWait = RegenDelay(); ToLoco(); PlayClip("idle", 0, R("blend.toIdleS", 0.1f)); /* DATALAYER b10 */ f.holdLast = false;
    }
    public void Press(string what)
    {
        if (IsPlayer && MarketSafe.Refuses(what)) return;   // STALL v1: no attack presses in the camp (dodges stay)
        int ttl = CombatFeel.QueueTtl(this, (int)R("bufferTtl", 11));   // P1: while a move / dodge / stagger runs, the press lives until the move can take it (cancel.json queue)
        if (ShoveTable.IsMove(what)) { pMove = what; pMoveTtl = ttl; return; }   // SHOVE v1: a shove.json move id is queued like a special; TickFree starts it (no special cooldown, no stamina refund)
        if (what == "special1" || what == "special2" || what == "special3" /* ABILITIES v1 */ || BellyBite.IsPress(what) /* BELLYBITE v1 */ || what == "light_left" || what == "light_right" || what == "heavy_overhead") { pMove = what == "special1" ? SpecialId(0) : what == "special2" ? SpecialId(1) : what == "special3" ? SpecialId(2) : what; /* DATALAYER b6 */ pMoveTtl = ttl; return; }
        switch (what) { case "sidestep": sideDir = 0; pSide = ttl; break; case "backstep": pBack = ttl; break; case "light": pLight = ttl; break; case "heavy": pHeavy = ttl; break; case "thrust": pThrust = ttl; break; case "kick": pKick = ttl; break; case "roll": pRoll = ttl; break; }
    }

    // sidestep request: dir -1 = left, +1 = right, 0 = back, relative to the opponent (dodge.json sidestep)
    public void PressSidestep(int dir) { sideDir = dir < 0 ? -1 : (dir > 0 ? 1 : 0); pSide = CombatFeel.QueueTtl(this, (int)R("bufferTtl", 11)); }

    // P1: true for the fighter a PlayerBrain drives (cancel.json scope "player"); sim ticks left in the current state (press queue length)
    bool isPlayer, isPlayerSet;
    public bool IsPlayer { get { if (!isPlayerSet) { isPlayerSet = true; isPlayer = GetComponent<PlayerBrain>() != null; } return isPlayer; } }
    // P1 hold-to-heavy: the attack key is still held after a light / stab fired on key-down -> turn that wind-up into the heavy (no pose hold: the heavy clip crossfades in from the swing).
    // Only inside the wind-up (t <= maxTick); the light's stamina is given back since the heavy charges its own.
    public bool UpgradeToHeavy(int maxTick)
    {
        if (state != FState.Windup || move == null || move.charges || move.id == "heavy_overhead" || t > maxTick) return false;
        stamina = Mathf.Min(maxStamina, stamina + move.stamina);
        StartMove("heavy_overhead", false);
        return true;
    }
    public int TicksLeft()
    {
        switch (state)
        {
            case FState.Windup: return move == null ? 0 : (Tk(move.Windup(chained)) - t) + Tk(move.Active(chained)) + Tk(move.Recovery(chained));
            case FState.Active: return move == null ? 0 : (Tk(move.Active(chained)) - t) + Tk(move.Recovery(chained));
            case FState.Recovery: return move == null ? 0 : Tk(move.Recovery(chained)) - t;
            case FState.ParryRecover: return ParryRecoverLen() - t;   // lanePARRY: parry or whiff recovery
            case FState.Stagger: return staggerTicks - t;
            case FState.Roll: return dodgeTicks - t;
        }
        return 0;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>(); hp = GetComponent<Health>(); f = GetComponent<Fighter>(); scale = transform.lossyScale.y;
        maxStamina = StaminaTable.MaxStamina(CharKey(), maxStamina); health = maxHealth; stamina = maxStamina; hp.max = maxHealth; hp.current = maxHealth;
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

    public string CharacterId { get { return CharKey(); } }   // roster id of this fighter ("" = not in roster.json)
    string CharKey()
    {
        if (charKey == null) charKey = Roster.IdOf(transform);   // own name first, then root name (roster.json objectName / id)
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
        int w = Tk(m.Windup(isChained)), a = Tk(m.Active(isChained)), r = Tk(m.Recovery(isChained)), total = w + a + r;
        float contactTick = Mathf.Max(1f, w + df.contactActiveFrac * a);
        float windS = contactTick / 60f, recS = Mathf.Max(1f, total - contactTick) / 60f;
        int cham = Tk((int)m.chamber);
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
        return (d > 0f ? d : DodgeTable.Get().minSeparation + 0.05f) + SwingFix.PadM();   // SWINGFIX v103: lunge stops where the padded clamp starts   // never lunge into the separation clamp (that pushed the opponent / skated)
    }

    // ---------------- tick (deterministic, 60 Hz) ----------------
    public void Tick()
    {
        if (state == FState.Dead) { if (reviveReq) ReviveTick(); return; }   // REVIVE v1: a queued respawn stands the fighter up at the top of a sim tick (DuelFighterRevive.cs)
        WoundSystem.SimTick(this);   // FIXPHYS3 v102: damage-state multipliers advance per sim tick, never per frame
        StaminaTick();
        PostureTick();
        if (parryCd > 0) parryCd--;
        if (riposteTicks > 0) riposteTicks--;
        if (blockFlash > 0) blockFlash--;
        if (special1Cd > 0) special1Cd--; if (special2Cd > 0) special2Cd--; if (special3Cd > 0) special3Cd--; if (special4Cd > 0) special4Cd--; if (evadeLeft > 0) evadeLeft--; /* ABILITIES v1 */ if (pMoveTtl > 0) pMoveTtl--; else pMove = null; if (pBack > 0) pBack--;
        if (pLight > 0) pLight--; if (pHeavy > 0) pHeavy--; if (pThrust > 0) pThrust--; if (pKick > 0) pKick--; if (pRoll > 0) pRoll--;
        if (pSide > 0) pSide--; if (dodgeCd > 0 && state != FState.Roll) dodgeCd--;
        if (state == FState.Guard) { float gd = DodgeTable.Get().guard.staminaDrainPerSecond; if (gd > 0f) stamina = Mathf.Max(0f, stamina - gd / 60f); }   // data, OFF at 0
        t++;
        FlowTickPre(); PoiseTick();   // V64FLOW + V64P
        bool wasPaused = paused;
        switch (state)
        {
            case FState.Loco: case FState.Guard: TickFree(); break;
            case FState.Windup: TickWindup(); break;
            case FState.Active: TickActive(); break;
            case FState.Recovery: TickRecovery(); break;
            case FState.Roll: if (t >= dodgeTicks) { dodgeCd = dodgeCdTicks; ToLoco(); } break;
            case FState.ParryRecover: if (t >= ParryRecoverLen()) { parryWhiffLen = 0; ToLoco(); } break;   // lanePARRY
            case FState.Stagger: if (t >= staggerTicks) ToLoco(); break;
            case FState.Down:
                if (fakeOn) { FakeTick(); break; }   // FAKEDEATH v1
                if (t == 54) PlayClip("get_up", 36);
                if (t >= R("posture.stun", 90)) ToLoco(); break;
        }
        if (atkOn && (state == FState.Windup || state == FState.Active || state == FState.Recovery))
        {
            if (atkFresh) atkFresh = false; else if (!(wasPaused && paused)) atkAge++;   // age = sim ticks played (stands still while the charge is parked)
            AtkSpeedTick();
        }
        DirTick();   // CTRL v68: guard-side history (DuelFighterDir.cs)
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
        if (guardHeld && (stamina > 0f || StaminaTable.Get().guard.allowAtZero || !StaminaTable.Get().enabled) && !runCancel)
        {
            if (state != FState.Guard)
            {
                state = FState.Guard; t = 0; guardAge = 0; parryWindow = parryCd == 0; parryCd = ParryCooldown(); ParryGuardRaised();   // lanePARRY
            }
            guardAge++;
        }
        else if (state == FState.Guard) { state = FState.Loco; t = 0; if (ParryWhiffOnDrop()) return; }   // lanePARRY: a whiffed parry tap

        if (pMove != null && ShoveTable.IsMove(pMove)) { string shoveId = pMove; pMove = null; StartMove(shoveId, false); return; }   // SHOVE v1
        if (pMove != null)
        {
            string id = pMove; pMove = null; var abr = AbilityTable.RowOfMove(this, id); int asl = abr != null ? abr.slot : -1;   // ABILITIES v1: abr null = not an ability (off, a monster, any other move): the old path, unchanged
            if (id == SpecialId(0) && special1Cd > 0) { } /* DATALAYER b6 */
            else if (id == SpecialId(1) && special2Cd > 0) { } /* DATALAYER b6 */
            else if (asl == 2 && special3Cd > 0) { }   // ABILITIES v1: shield bash cooling down
            else if (asl >= 0 && AbilityPoor(id)) { }   // ABILITIES v1: priced ability, not enough stamina: refused like a cooling one
            else { int acd = AbilityTable.CooldownTicks(abr, (int)R("special.cooldownTicks", 540)); if (id == SpecialId(0)) special1Cd = acd; else if (id == SpecialId(1)) special2Cd = acd; else if (asl == 2) special3Cd = acd; /* DATALAYER b6 */ stamina += moves.Get(id) != null ? moves.Get(id).stamina : 0; StartMove(id, false); return; }
        }
        if (dodgeCd <= 0)
        {
            if (pBack > 0 && stamina >= BackstepCost()) { StartBackstep(); return; }
            if (pSide > 0 && stamina >= SidestepCost()) { StartSidestep(); return; }
            if (pRoll > 0 && stamina >= RollCost()) { StartRoll(); return; }
        }
        if (FlowRiposteFree()) return;   // V64FLOW: dedicated riposte key
        if (riposteTicks > 0 && FlowRiposteAuto() && (pLight > 0 || pHeavy > 0 || pThrust > 0)) { StartMove(AiAdapt.RiposteMove(this, pLight > 0, pHeavy > 0, pThrust > 0, ref riposteTicks), false); return; }   // V63: data ai_adapt.json riposte{}
        if (pHeavy > 0) { if (HeavyAllowed()) { StartMove(DirPick("heavy", "heavy_overhead"), false); return; } pLight = Mathf.Max(pLight, pHeavy); pHeavy = 0; }
        if (pLight > 0) { StartMove(DirPick("light", lastLightRight ? "light_left" : "light_right"), false); return; }   // CTRL v68: DirPick = the directed press (DuelFighterDir.cs)
        if (pThrust > 0) { StartMove(DirPick("thrust", "thrust"), false); return; }
        if (pKick > 0) { StartMove("kick", false); return; }
    }

    // ---------------- dodges (dodge.json; "enabled": false falls back to the V3 rules-defs values) ----------------
    float RollCost() { var d = DodgeTable.Get(); return FlowDodgeCost(StaminaTable.DodgeCost("roll", d.enabled ? d.roll.stamina : R("rollCost", 30f))); }
    float BackstepCost() { var d = DodgeTable.Get(); return FlowDodgeCost(StaminaTable.DodgeCost("backstep", d.enabled ? d.backstep.stamina : R("backstep.cost", 10f))); }
    float SidestepCost() { return FlowDodgeCost(StaminaTable.DodgeCost("sidestep", DodgeTable.Get().sidestep.stamina)); }

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
        dodgePow = Mathf.Max(1f, pow); dodgeCdTicks = Mathf.Max(0, cd); dodgeTurn = turn; dodgeAge = 0f; if (state == FState.Recovery) statCancelDodge++; FlowDodgeBegin(kind);   // V64P: cancel counter
        state = FState.Roll; t = 0; pRoll = 0; pBack = 0; pSide = 0; atkOn = false; fadeOutLen = 0f;
    }

    void PlayDodgeClip(string clip, DodgeTable.DodgeRow r)
    {
        if (r.blendIn > 0f && HasState(clip))   // P1: dodge.json blendIn = short, explicit crossfade (input-to-motion latency)
        {
            f.holdLast = false; paused = false; f.ForceExact(clip, r.blendIn);
            float len = ClipLen(clip);
            f.anim.speed = (r.clipSpeed > 0f ? r.clipSpeed : (len > 0f ? len / ((r.ticks + r.clipTailTicks) / 60f) : 1f)) * f.animWeight;
            return;
        }
        if (r.clipSpeed > 0f) { PlayClip(clip, 0); f.anim.speed = r.clipSpeed * f.animWeight; }
        else PlayClip(clip, r.ticks + r.clipTailTicks);
    }

    void StartRoll()
    {
        var d = DodgeTable.Get(); var r = d.roll;
        int ticks = (int)R("roll", 36), isf = (int)R("safeStart", 4), ief = (int)R("safeEnd", 20); float dist = 3.2f, cost = R("rollCost", 30f), pow = 1f, turn = 12f; int cd = 0;
        if (d.enabled) { ticks = r.ticks; isf = r.iframeStart; ief = r.iframeEnd; dist = r.distance; cost = RollCost(); pow = r.easePower; turn = r.turnRate; cd = r.cooldownTicks; }
        stamina = Mathf.Max(0f, stamina - cost); regenWait = RegenDelay();
        if (moveDir.sqrMagnitude > 0.01f) rollDir = moveDir.normalized;
        else
        {
            string nd = d.enabled ? r.noInputDir : "back";
            Vector3 fw = transform.forward; fw.y = 0f; if (fw.sqrMagnitude < 1e-4f) fw = Vector3.forward;
            rollDir = nd == "forward" ? fw.normalized : (nd == "away" ? -OppFlat() : -fw.normalized);
        }
        rollDir.y = 0f; FlowRollAxis();   // V64FLOW
        BeginDodge(0, ticks, dist, isf, ief, pow, cd, turn);
        string clip = (d.enabled && !string.IsNullOrEmpty(r.clip)) ? r.clip : "roll_forward";
        if (!HasState(clip)) { clip = "roll_forward"; if (d.enabled) { var o = new DodgeTable.DodgeRow { ticks = ticks, clipTailTicks = 0 }; PlayDodgeClip(clip, o); } else PlayClip(clip, ticks); }   // character without the baked dodge clip (no dodge_clips bake row): old roll clip
        else if (d.enabled) PlayDodgeClip(clip, r); else PlayClip(clip, ticks);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " ROLL dir=" + rollDir + " ticks=" + ticks + " dist=" + dist + " stamina=" + stamina);
        ContactRelay.Action(this, (d.enabled && !string.IsNullOrEmpty(r.fxAction)) ? r.fxAction : "roll");
        ContactRelay.Dodge(this, "roll", rollDir); LatencyProbe.Started(this, "dodge", "roll");
    }

    void StartBackstep()
    {
        var d = DodgeTable.Get(); var r = d.backstep;
        int ticks = (int)R("backstep.ticks", 12), isf = (int)R("safeStart", 4), ief = (int)R("safeEnd", 20); float dist = 1.4f, cost = R("backstep.cost", 10f), pow = 1f; int cd = 0;
        if (d.enabled) { ticks = r.ticks; isf = r.iframeStart; ief = r.iframeEnd; dist = r.distance; cost = BackstepCost(); pow = r.easePower; cd = r.cooldownTicks; }
        stamina = Mathf.Max(0f, stamina - cost); regenWait = RegenDelay();
        rollDir = -OppFlat();   // straight away from the opponent (the old -transform.forward is the same while he is faced); the body keeps facing him
        BeginDodge(1, ticks, dist, isf, ief, pow, cd, R("dodge.turnRate", 12f)); /* DATALAYER b10 */
        string clip = (d.enabled && !string.IsNullOrEmpty(r.clip)) ? r.clip : "backstep";
        if (d.enabled) PlayDodgeClip(clip, r); else PlayClip(clip, ticks + 30);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " BACKSTEP ticks=" + ticks + " stamina=" + stamina);
        ContactRelay.Action(this, (d.enabled && !string.IsNullOrEmpty(r.fxAction)) ? r.fxAction : "backstep");
        ContactRelay.Dodge(this, "backstep", rollDir); LatencyProbe.Started(this, "dodge", "backstep");
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
        stamina = Mathf.Max(0f, stamina - SidestepCost()); regenWait = RegenDelay();
        BeginDodge(2, r.ticks, r.distance, r.iframeStart, r.iframeEnd, r.easePower, r.cooldownTicks, R("dodge.turnRate", 12f)); /* DATALAYER b10 */
        PlayDodgeClip(clip, r);
        if (d.debugLog) Debug.Log("[Dodge] " + name + " SIDESTEP dir=" + sideDir + " clip=" + clip + " stamina=" + stamina);
        ContactRelay.Action(this, string.IsNullOrEmpty(r.fxAction) ? "backstep" : r.fxAction);
        ContactRelay.Dodge(this, "sidestep", rollDir); LatencyProbe.Started(this, "dodge", "sidestep");
    }

    void StartMove(string id, bool isChained)
    {
        var m = WeaponMove(id); if (m == null) return;   // WEAPONMOVES v1
        move = m; chained = isChained; state = FState.Windup; t = 0; hitDone = false; charge = 0;
        pLight = pHeavy = pThrust = pKick = 0;
        if (id == "light_right") lastLightRight = true; else if (id == "light_left") lastLightRight = false;
        stamina = Mathf.Max(0f, stamina - MoveCost(m)); regenWait = RegenDelay();
        int total = Tk(m.Windup(isChained)) + Tk(m.Active(isChained)) + Tk(m.Recovery(isChained));
        var arow = AttackClipTable.Get().enabled ? AttackClipTable.Find(CharKey(), id) : null;
        fadeOutLen = 0f;
        if (!BeginAttackClip(arow, m, isChained)) PlayClip(AbilityTable.ClipOf(this, id) ?? ClipFor(id) /* ABILITIES v1 */, total, R("blend.attackSquashS", 0.06f)) /* DATALAYER b10 */;   // no row / state: old whole-clip squash
        FlowAttackStart(m);   // V64FLOW
        ContactRelay.AttackStarted(this, m);
        LatencyProbe.Started(this, "attack", id);
    }

    void TickWindup()
    {
        int w = Tk(move.Windup(chained));
        if (move.charges && t >= Tk((int)move.chamber) && heavyHeld && charge < (int)R("charge.max", 54))
        {
            charge++; t = Tk((int)move.chamber); if (!paused) { paused = true; f.anim.speed = 0f; }
            return;
        }
        if (paused) { paused = false; if (!atkOn) f.anim.speed = ClipLen("heavy_release") / ((w + Tk(move.Active(chained)) + Tk(move.Recovery(chained))) / 60f); }
        if (t >= w) { state = FState.Active; t = 0; }
    }

    void TickActive()
    {
        if (!hitDone && opponent != null)
        {
            float mul = (move.charges && charge >= (int)R("charge.min", 30)) ? R("charge.damage", 1.5f) : 1f;
            if (Duel.Resolve(this, opponent, move, mul)) hitDone = true;
        }
        if (t >= Tk(move.Active(chained))) { if (!hitDone) ContactRelay.AttackMissed(this, move); state = FState.Recovery; t = 0; }
    }

    void TickRecovery()
    {
        if (!FlowChainOwns() && move.chainFollow != null && t <= move.chainWindow)
        {
            if (pHeavy > 0 && System.Array.IndexOf(move.chainFollow, "heavy_overhead") >= 0) { StartMove("heavy_overhead", true); return; }
            if (pLight > 0) { foreach (var fid in move.chainFollow) if (fid.StartsWith("light")) { StartMove(fid, true); return; } }
        }
        if (FlowChain()) return;   // V64FLOW: attack-to-attack chain rows (flow.json chain)
        if (CombatFeel.Applies(this)) { if (TryCancel()) return; }   // P1: cancel windows from cancel.json (roll / sidestep / backstep / guard / buffered attacks at the earliest legal tick)
        else if (dodgeCd <= 0 && t > Tk(move.Recovery(chained)) / 2)
        {
            if (pSide > 0 && stamina >= SidestepCost()) { StartSidestep(); return; }
            if (pRoll > 0 && stamina >= RollCost()) { StartRoll(); return; }
        }
        if (t >= Tk(move.Recovery(chained))) ToLoco();
    }

    // P1 cancel windows: a buffered action runs on the first Recovery tick the table allows (after the active window), not only when the recovery has finished.
    bool TryCancel()
    {
        string mid = move.id; int e;
        if (dodgeCd <= 0)
        {
            if (pBack > 0 && Can(mid, "backstep", out e) && stamina >= BackstepCost()) { StartBackstep(); return true; }
            if (pSide > 0 && Can(mid, "sidestep", out e) && stamina >= SidestepCost()) { StartSidestep(); return true; }
            if (pRoll > 0 && Can(mid, "roll", out e) && stamina >= RollCost()) { StartRoll(); return true; }
        }
        bool queued = pMove != null || pHeavy > 0 || pLight > 0 || pThrust > 0 || pKick > 0;
        if (!queued && guardHeld && stamina > 0f && Can(mid, "guard", out e)) { statCancelGuard++; ToLoco(); TickFree(); return true; }   // V64P   // TickFree enters Guard
        bool atk;
        if (pMove != null) atk = Can(mid, "special", out e) && (pMove == SpecialId(0) ? special1Cd <= 0 : pMove == SpecialId(1) ? special2Cd <= 0 : pMove == SpecialId(2) ? special3Cd <= 0 /* ABILITIES v1 */ : true) /* DATALAYER b6 */;
        else atk = (pHeavy > 0 && Can(mid, "heavy", out e)) || (pLight > 0 && Can(mid, "light", out e)) || (pThrust > 0 && Can(mid, "thrust", out e)) || (pKick > 0 && Can(mid, "kick", out e));
        if (atk) { statCancelAtk++; ToLoco(); TickFree(); return true; }   // V64P   // TickFree starts the buffered move (riposte / heavy / light / thrust / kick / special)
        return false;
    }
    bool Can(string moveId, string to, out int e)
    {
        e = CombatFeel.Earliest(moveId, to);
        if (e < 0 || t < e) return false;
        if (CombatFeel.OnHitOnly(moveId, to) && !hitDone) return false;
        return true;
    }

    // ---------------- reactions (called by Duel.Resolve) ----------------
    public void Stagger(int ticks, string clip, float startS = 0f)   // PITMAULFIX v1: startS
    {
        if (state == FState.Dead) return;
        atkOn = false; fadeOutLen = 0f; state = FState.Stagger; t = 0; staggerTicks = Mathf.Max((int)R("stagger.minTicks", 8), ticks); /* DATALAYER b10 */ pLight = pHeavy = pThrust = pKick = 0;
        if (startS > 0f && f != null && f.anim != null) { f.holdLast = false; paused = false; f.ForceExact(clip, R("blend.staggerS", 0.05f), startS); f.anim.speed = f.animWeight; }   // PITMAULFIX v1: react from startS into the clip
        else PlayClip(clip, 0, R("blend.staggerS", 0.05f)); /* DATALAYER b10 */
    }
    public void KnockDown()
    {
        if (state == FState.Dead) return;
        atkOn = false; fadeOutLen = 0f; state = FState.Down; t = 0; posture = 0f; PlayClip("knockdown", 54, 0.05f);
    }
    public void Die()
    {
        if (FakeDeath.On) { if (!fakeOn && !RTFinisher.Busy && FakeDeathTry()) return; if (fakeOn) FakeCancelForDie(); }   // FAKEDEATH v1: a boss falls instead of dying (once per run), a real death while it is down wins
        FState diedFrom = state;   // DEATHFIX v92: the state the killing blow found (Down = already on the floor)
        atkOn = false; fadeOutLen = 0f; state = FState.Dead; t = 0; health = 0f; hp.current = 0f; ContactRelay.Killed(this, move);
        DeathPlay(diedFrom); f.holdLast = true;   // DEATHFIX v92: was PlayClip("death", 0, 0.08f), which played the clip's ~1.1 s upright lead-in at 0 HP
        var col = GetComponent<Collider>(); if (col) col.enabled = false;
        hp.onDied.Invoke();
    }
    // DEATHFIX v92: the death clip (Mixamo, 4.4 s) keeps the hips at standing height for ~1.2 s before the knees buckle, so a fighter at 0 HP stood (and a body killed while knocked down
    // popped upright first). death.json (per character row, "" = default row) states the clip, the start second inside it, the speed and the crossfade; the Down variant starts at the
    // clip's floor pose. No row / file / state in the controller = the old behaviour (clip "death" from 0, 0.08 s fade).
    void DeathPlay(FState from)
    {
        var r = DeathTable.Find(CharKey());
        if (r == null || f == null || f.anim == null) { PlayClip("death", 0, R("blend.deathS", 0.08f)); return; /* DATALAYER b10 */ }
        string clip = HasState(r.clip) ? r.clip : "death";
        bool down = from == FState.Down && r.downEnabled;
        float start = down ? r.downStartSeconds : r.startSeconds, fade = down ? r.downFade : r.fade;
        float len = ClipLen(clip);
        if (len > 0.1f) start = Mathf.Clamp(start, 0f, len - 0.05f); else start = 0f;
        f.holdLast = false; paused = false;
        f.ForceExact(clip, Mathf.Max(0f, fade), start);
        f.anim.speed = Mathf.Max(0.05f, r.speed) * f.animWeight;
    }
    public void ParrySuccess() { parryGuardContact = true; parryWhiffLen = 0; atkOn = false; fadeOutLen = 0f; state = FState.ParryRecover; t = 0; riposteTicks = ParryRiposteWindow(); ParryNote(); PlayClip("parry", (int)ParrySelfRecovery() + 10, 0.04f); }
    public void BlockHit(bool heavy) { blockFlash = 10; f.Force("guard_hit", 0.04f); f.anim.speed = 1.4f; }
    public void AddPosture(float p) { posture = Mathf.Min(posture + p, PostureMax); postureHold = PostureTable.HoldTicks(CharKey(), (int)R("posture.hold", 60)); if (p > 0f) statPostureGained += p; }
    public void SetKnock(Vector3 v) { knock = v; }
    public Vector3 KnockVelocity => knock;   // PHYS1 v1: read-only copy for the death ragdoll (presentation never writes the sim)
    public bool InParryWindow => state == FState.Guard && parryWindow && guardAge <= ParryWindowTicks() && !(exhausted && StaminaTable.Get().exhausted.noParry);
    public bool IsGuarding => state == FState.Guard && guardAge >= WoundSystem.GuardRaiseExtra(this) && (!exhausted || guardAge > StaminaTable.Get().guard.raiseDelayExhausted);
    public bool IFrames => state == FState.Roll && t >= dodgeIS && t <= dodgeIE;   // set per move by BeginDodge (dodge.json iframeStart/End)
    public bool InWindup => state == FState.Windup;
    // ---------------- stamina / guard / parry (P5; data: stamina.json, parry.json via StaminaTable) ----------------
    bool exhausted; int guardBreakFlash, parryOkFlash; float stLast;
    public float statSpent; public int statParries, statBlocks, statGuardBreaks, statExhaustEntries, statExhaustTicks, statHeavyDowngrades;   // balance counters (BalanceAuto reads these)
    public bool Exhausted => exhausted;
    public int GuardBreakFlash => guardBreakFlash;
    public int ParryFlash => parryOkFlash;
    public int ParryWindowTicks() { return Mathf.Max(1, Mathf.RoundToInt(StaminaTable.ParryWindow(CharKey(), (int)R("parry", 10)) * StanceLayer.ParryWindowMul(this))); }   // STANCE2 v81: stances.json parryWindowMul (1 = unchanged)
    public int ParryWindowLeft => InParryWindow ? Mathf.Max(0, ParryWindowTicks() - guardAge) : 0;
    int RegenDelay() { var S = StaminaTable.Get(); return !S.enabled ? (int)R("regenDelay", 45) : (exhausted ? S.regen.exhaustedDelayTicks : S.regen.delayTicks); }
    int ParryCooldown() { var P = StaminaTable.Parry(); return P.enabled ? P.cooldownTicks : (int)R("parryCooldown", 30); }
    float ParrySelfRecovery() { var P = StaminaTable.Parry(); return P.enabled ? P.selfRecoveryTicks : R("parryRecovery", 8); }
    int ParryRiposteWindow() { var P = StaminaTable.Parry(); return P.enabled ? P.riposteWindowTicks : (int)R("parryStun", 90); }
    void ParryNote() { var P = StaminaTable.Parry(); if (P.enabled) { stamina = Mathf.Clamp(stamina - P.staminaCost + P.staminaGain, 0f, Mathf.Max(stamina, maxStamina)); if (P.staminaCost > 0f) regenWait = RegenDelay(); } parryOkFlash = StaminaTable.Get().hud.parryFlashTicks; statParries++; }   // lanePARRY: staminaGain = the parry reward
    float MoveCost(MoveDef m) { float ac = AbilityTable.CostOf(this, m.id, -1f); /* ABILITIES v1: -1 = not a priced ability */ return (ac >= 0f ? ac : StaminaTable.MoveCost(m)) * StanceLayer.AttackCostMul(this) * (m.weaponStaminaMul > 0f ? m.weaponStaminaMul : 1f); }   // WEAPONMOVES v1   // STANCE2 v81: stances.json attackCostMul
    public float MoveMulNow => SpeedMulExhausted();   // CHASE v1: the player's effective run speed is the chase speed reference
    float SpeedMulExhausted() { return (exhausted ? StaminaTable.Get().exhausted.walkMul : 1f) * WoundSystem.MoveMul(this) * StanceLayer.MoveSpeedMul(this); }   /* STANCE2 v81: stances.json moveSpeedMul */   // lane WOUND: limp (HP stages) and leg-hit slow, 1 when healthy / off
    public float DamageScaleNow => damageScale * (exhausted ? StaminaTable.Get().exhausted.damageMul : 1f) * WeaponHooks.DamageMul(this); /* WEAPONSWAP v88: weapon damage */
    bool HeavyAllowed() { var S = StaminaTable.Get(); bool ok = !S.enabled || (!exhausted && stamina >= S.exhausted.heavyMinStamina); if (!ok) statHeavyDowngrades++; return ok; }
    public void NoteBlock() { statBlocks++; parryGuardContact = true; }
    public void GuardBreakStun(int ticks = -1)
    {
        var S = StaminaTable.Get(); var G = S.guardBreak;
        if (ticks <= 0) stamina = S.enabled ? G.staminaAfter : 0f; guardBreakFlash = G.flashTicks; statGuardBreaks++;
        if (S.debugLog) Debug.Log("[Stamina] " + name + " GUARD BREAK tick=" + Duel.TickCount);
        Stagger(ticks > 0 ? ticks : (S.enabled ? G.staggerTicks : 60), "stagger_loop");
    }
    void StaminaTick()
    {
        var S = StaminaTable.Get();
        if (guardBreakFlash > 0) guardBreakFlash--; if (parryOkFlash > 0) parryOkFlash--;
        if (!S.enabled) { if (regenWait > 0) regenWait--; else stamina = Mathf.Min(maxStamina, stamina + R("regen", 0.667f)); return; }
        if (regenWait > 0) regenWait--;
        else
        {
            float mul = StaminaTable.RegenMul(CharKey()) * GearHooks.StaminaRegenMul(this); /* kitGEAR v79: weight stat */ if (state == FState.Guard) mul *= S.regen.guardMul; if (exhausted) mul *= S.regen.exhaustedMul;
            mul *= WoundSystem.RegenMul(this);   // lane WOUND: heavy breathing when hurt
            stamina = Mathf.Min(maxStamina * WoundSystem.StaminaCeil(this), stamina + S.regen.perSecond * mul / 60f);
        }
        if (state == FState.Guard && guardAge > S.guard.holdFreeTicks && S.guard.holdDrainPerSecond > 0f) stamina = Mathf.Max(0f, stamina - S.guard.holdDrainPerSecond / 60f);
        if (!exhausted && stamina <= 0f)
        {
            exhausted = true; statExhaustEntries++; regenWait = Mathf.Max(regenWait, S.regen.exhaustedDelayTicks);
            if (S.debugLog) Debug.Log("[Stamina] " + name + " EXHAUSTED tick=" + Duel.TickCount);
        }
        else if (exhausted && stamina >= S.exhausted.recoverAt) exhausted = false;
        if (exhausted) statExhaustTicks++;
        float cur = Mathf.Max(0f, stamina); if (cur < stLast) statSpent += stLast - cur; stLast = cur;
    }
        // ---------------- posture (P6; data: posture.json via PostureTable. "enabled": false = the old V3 rules) ----------------
    int deathWindow, deathTotal, breakFlash; bool postureBroken;
    public int statPostureBreaks, statDeathblows, statDeathblowsTaken, statDeathblowsMissed; public float statPostureGained;   // balance counters (BalanceAuto reads these): breaks suffered, deathblows dealt / taken, windows that ran out unused
    public string PostureKey => CharKey();
    public float PostureMax => PostureTable.Max(CharKey(), R("posture.max", 100f));
    public bool PostureBroken => postureBroken && state != FState.Dead;
    public bool DeathblowOpen => deathWindow > 0 && state != FState.Dead && PostureTable.Get().enabled;
    public int DeathblowLeft => deathWindow;
    public int DeathblowTotal => deathTotal;
    public int DeathblowAge => deathTotal - deathWindow;
    public int PostureBreakFlash => breakFlash;
    public void RelievePosture(float p) { posture = Mathf.Max(0f, posture - p); }
    void PostureTick()
    {
        var P = PostureTable.Get();
        if (breakFlash > 0) breakFlash--;
        if (!P.enabled) { if (posture > 0) { if (postureHold > 0) postureHold--; else posture = Mathf.Max(0f, posture - R("posture.decay", 0.2f)); } return; }
        if (postureBroken)
        {
            if (deathWindow > 0) deathWindow--;
            if (deathWindow <= 0 || posture <= 0f) EndPostureBreak(false);   // window over (or something reset posture, e.g. a knockdown / finisher cleanup)
            return;                                                          // posture holds at max while broken
        }
        if (posture >= PostureMax) { BeginPostureBreak(); return; }          // posture added without Duel.Resolve (steer reactions)
        if (posture > 0f) { if (postureHold > 0) postureHold--; else posture = Mathf.Max(0f, posture - PostureTable.DecayPerTick(CharKey(), R("posture.decay", 0.2f))); }
    }
    // called by Duel.Resolve right after posture was added; true when this fighter just broke
    public bool TryPostureBreak(DuelFighter by)
    {
        if (!PostureTable.Get().enabled || postureBroken || state == FState.Dead || posture < PostureMax) return false;
        BeginPostureBreak(); return true;
    }
    void BeginPostureBreak()
    {
        var P = PostureTable.Get(); var B = P.posBreak;
        postureBroken = true; posture = PostureMax; deathTotal = Mathf.Max(1, B.windowTicks); deathWindow = deathTotal; breakFlash = B.flashTicks; statPostureBreaks++;
        if (P.debugLog) Debug.Log("[Posture] " + name + " POSTURE BREAK tick=" + Duel.TickCount + " window=" + deathTotal);
        Stagger(B.stunTicks, string.IsNullOrEmpty(B.clip) ? "stagger_loop" : B.clip);
    }
    void EndPostureBreak(bool consumed)
    {
        var P = PostureTable.Get();
        if (!consumed && deathWindow <= 0) statDeathblowsMissed++;
        postureBroken = false; deathWindow = 0; posture = PostureMax * Mathf.Clamp01(P.posBreak.resetFraction); postureHold = PostureTable.HoldTicks(CharKey(), (int)R("posture.hold", 60));
    }
    // the next hit on a broken fighter landed (Duel.Resolve): count it for both sides and close the window
    public void ConsumeDeathblow(DuelFighter by)
    {
        if (!postureBroken) return;
        statDeathblowsTaken++; if (by != null) by.statDeathblows++;
        if (PostureTable.Get().debugLog) Debug.Log("[Posture] DEATHBLOW on " + name + " tick=" + Duel.TickCount + " windowLeft=" + deathWindow);
        EndPostureBreak(true);
    }
    public float Rule(string k, float d) { return R(k, d); }
    float cFaceRange = float.NaN, cStrafeRatio = float.NaN, cLocoBlend = float.NaN; /* DATALAYER b10 */
    float Rf(ref float c, string k, float d) { if (!float.IsNaN(c)) return c; if (!rules) return d; return c = rules.N(k, d); } /* DATALAYER b10 */ // read once per fighter, never cached while rules is unbound
    float FaceRangeM { get { return Rf(ref cFaceRange, "face.rangeM", 14f); } } /* DATALAYER b10 */
    float StrafeRatio { get { return Rf(ref cStrafeRatio, "strafe.ratio", 0.9f); } } /* DATALAYER b10 */
    float cHitArc = float.NaN, cHeavyDmg = float.NaN; /* DATALAYER b11 */
    public float HitArcDeg { get { return Rf(ref cHitArc, "hit.arcDeg", 70f); } } /* DATALAYER b11 */
    public float HeavyDamage { get { return Rf(ref cHeavyDmg, "hit.heavyDamage", 18f); } } /* DATALAYER b11 */
    float LocoBlendS { get { return Rf(ref cLocoBlend, "blend.locoS", 0.15f); } } /* DATALAYER b10 */
    public void SpendStamina(float s) { stamina -= s; regenWait = RegenDelay(); }

    // ---------------- presentation (per frame) ----------------
    // PHYSAUDIT: body motion is SIM state. It ran in Update with Time.deltaTime (one cc.Move per rendered frame), so frame rate and hit-stop/physics frame-time wobble changed positions.
    // Now ONE move per 60 Hz sim tick (Duel.Update calls MoveTick for each fighter after all Ticks); the view interpolates separately (ViewInterp). No Time.* inside.
    public void MoveTick()
    {
        const float dt = 1f / 60f;
        Vector3 v = Vector3.zero;
        Vector3 toOpp = opponent ? opponent.transform.position - transform.position : transform.forward; toOpp.y = 0f;
        float dist = toOpp.magnitude; Vector3 dir = dist > 0.01f ? toOpp / dist : transform.forward;
        bool faceOpp = opponent && !opponent.Dead && dist < FlowFaceRange(FaceRangeM) /* DATALAYER b10 */ && FlowLockOk();   // V64FLOW
        switch (state)
        {
            case FState.Loco: v = moveDir * (run ? runSpeed * MountRider.SpeedMul(this, true) : walkSpeed * MountRider.SpeedMul(this, false)) * SpeedMulExhausted(); break;   // FIX2 v1: the mount multiplier is applied here, on the sim tick (was: MountRider.Update wrote walkSpeed / runSpeed). 1.0 when not mounted
            case FState.Guard: { var dg = DodgeTable.Get(); v = moveDir * (walkSpeed * MountRider.SpeedMul(this, false)) * (dg.enabled ? dg.guard.speedMul : R("guardSpeed", 0.35f) * 1.6f) * SpeedMulExhausted(); break; }   // run is off while guarding (the branch never reads `run`)
            case FState.Roll:
            {
                // distance travelled follows 1-(1-x)^power over the move (power 1 = constant speed); sim time, so hit-stop freezes it
                float durS = dodgeTicks / 60f, a0 = Mathf.Clamp01(dodgeAge / durS);
                dodgeAge += dt;
                float a1 = Mathf.Clamp01(dodgeAge / durS);
                float step = (Mathf.Pow(1f - a0, dodgePow) - Mathf.Pow(1f - a1, dodgePow)) * dodgeDist * scale;
                v = rollDir * (step / Mathf.Max(dt, 1e-4f));
                if (dodgeKind == 0) faceOpp = false;   // a roll turns to its direction; backstep and sidestep keep facing the opponent
                break;
            }
            case FState.Windup:
                if (!PresentationClock.Frozen && t >= R("stepInFrom", 3) && move != null && dist > StepStopDist()) { float si = StepInFor(); if (si > 0f) v = FlowStepDir(dir) * (si * scale / Mathf.Max(0.1f, (Tk(move.Windup(chained)) - 3) / 60f)); }   // stepIn: data (attack_clips.json) or the move's value
                break;
            case FState.Stagger: case FState.Down: if (!PresentationClock.Frozen) { v = knock; knock *= HitKnockback.Decay;   /* KNOCKBACK v1: was the literal 0.88f, same default */ } faceOpp = false; break;   // P1: no knock slide while the animation is frozen (hit-stop)
            case FState.Dead: faceOpp = false; break;
        }
        if (PresentationClock.Frozen) { v.x = 0f; v.z = 0f; }   // P1: hit-stop freezes the body with the animation (the legs are frozen too: no ice-skating)
        vy = (cc.isGrounded || ZoneGroundHold.Active) ? -1f : vy - ArenaLimits.FallGravity * dt;   // CAMPGROUND v1: no free fall while a zone arrival waits for its ground ring
        v.y = vy; if (cc.enabled) cc.Move(v * dt);
        // kill-plane recovery (audit: a knockback pushed a fighter off the ground collider and it fell for ever, the camera followed): remember the last grounded position, restore it when far below
        if (cc.enabled && cc.isGrounded && transform.position.y > ArenaLimits.GroundedMinY) { lastGroundedPos = transform.position; hasGrounded = true; }
        if (hasGrounded && transform.position.y < lastGroundedPos.y - ArenaLimits.FallDropM)
        {
            float rnow = Time.unscaledTime; if (rnow - restoreWinStart > ArenaLimits.RestoreWindowS) { restoreWinStart = rnow; restoreCount = 0; } restoreCount++;   // ROBUST v1
            if (ArenaLimits.MaxRestores > 0 && restoreCount > ArenaLimits.MaxRestores && ZoneRuntime.RescuePlayer(this)) { lastGroundedPos = transform.position; restoreCount = 0; Debug.Log("[DuelFighter] " + name + " kept falling at the same spot: rescued to the zone arrive spot"); }   // the normal restore below then lands on the rescue spot
            cc.enabled = false; transform.position = lastGroundedPos + Vector3.up * ArenaLimits.RestoreUpM; cc.enabled = true; Physics.SyncTransforms(); vy = -1f; knock = Vector3.zero; snapView = true;
            Debug.Log("[DuelFighter] " + name + " fell out of the world: restored to " + lastGroundedPos);
        }
        float lim = ArenaLimits.StairHalfWidthX; /*HCKIT1 v93*/   // keep both fighters inside the stair width (no climbing onto the balustrade)
        if (cc.enabled && transform.position.z > ArenaLimits.StairZMin && transform.position.z < ArenaLimits.StairZMax && Mathf.Abs(transform.position.x) > lim && PlayArea.Row(transform.position.z) == null /*PLAYAREA v1*/) cc.Move(new Vector3(Mathf.Sign(transform.position.x) * lim - transform.position.x, 0f, 0f));

        // never overlap the opponent: keep a minimum horizontal separation (radii + 0.25 m), each fighter yields half of the overlap, in every state
        if (cc.enabled && opponent && state != FState.Dead && !opponent.Dead)
        {
            Vector3 oq = opponent.transform.position - transform.position; oq.y = 0f; float dq = oq.magnitude, minSep = Mathf.Max(DodgeTable.Get().minSeparation, radius + opponent.radius + DodgeTable.Get().separationPad) + SwingFix.ExtraSep(this, opponent);   // SWINGFIX v103: swing-phase pad (0 = off)
            if (dq < minSep) cc.Move((dq > 0.001f ? -oq / dq : -transform.forward) * ((minSep - dq) * 0.5f));
        }
        if (cc.enabled && state != FState.Dead && FoeSep.On) FoeSep.Separate(this, cc);   // FOESEP v1: the rear of two enemies on one target side-steps round it (zones, two fighting enemies only)
        if (FlowFace(dt, dir, dist, faceOpp)) { }   // V64FLOW
        else if (state == FState.Roll && dodgeKind == 0 && rollDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(rollDir), Mathf.Min(1f, dodgeTurn * dt));
        else if (faceOpp && state != FState.Stagger && state != FState.Down) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 10f * dt);
        else if (state == FState.Loco && moveDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), 10f * dt);
        if (Duel.BrainsOnTick) ArenaLimits.ClampTick(this);   // FPSSIM v1: arena rectangle on the sim tick (was ArenaLimits.LateUpdate, per frame)
        faceOppV = faceOpp;
        if (viewInterp == null) { viewInterp = GetComponent<ViewInterp>(); if (viewInterp == null) viewInterp = gameObject.AddComponent<ViewInterp>(); }
        viewInterp.Push(transform.position, snapView); snapView = false;
    }
    bool faceOppV, snapView; ViewInterp viewInterp;
    public readonly LocoDir.Meter locoDir = new LocoDir.Meter();   // LOCODIR v1: per-fighter velocity meter / clip pick / speed match (loco_dir.json; off or no row = today)
    public Vector3 ViewPosition { get { return viewInterp != null ? viewInterp.ViewPos : transform.position; } }   // for the camera / HUD (never the sim)

    void Update()
    {
        float dt = Time.deltaTime;   // presentation only (animation weights, guard layer)
        bool faceOpp = faceOppV;
        string upperPose = null;   // guard pose for the masked upper-body layer; null = layer off
        if (state == FState.Loco || (state == FState.Guard && blockFlash == 0))
        {
            f.anim.speed = f.animWeight;
            bool moving = moveDir.sqrMagnitude > 0.01f;
            if (moving) LatencyProbe.Started(this, "move", run ? "run" : "walk");
            bool guarding = state == FState.Guard;
            bool runNow = run && !guarding;   // no running while the guard is up
            string clip;
            string pose = DirectionalGuard.PoseOf(guardSide);   // GUARDLOW v1: side id -> pose state from controls.json guard.sides[].pose (was a hard-coded id map)
            if (guarding && pose != "guard_idle" && f.anim != null && !f.anim.HasState(0, Animator.StringToHash(pose))) pose = "guard_idle";   // a character whose controller has no clip for this side (e.g. guard_low not built yet) holds the centre guard instead of freezing on the last pose
            if (!moving) clip = guarding ? "guard_idle" : "idle";
            else
            {
                Vector3 lm = transform.InverseTransformDirection(moveDir);
                bool strafe = faceOpp && Mathf.Abs(lm.x) > Mathf.Abs(lm.z) * StrafeRatio && !runNow /* DATALAYER b10 */;
                clip = strafe ? (lm.x < 0 ? "strafe_L" : "strafe_R") : (runNow ? "run" : "walk");
            }
            if (moving && LocoDir.Enabled) clip = locoDir.Choose(this, transform, moveDir, faceOpp, runNow, guarding, clip, Time.time, dt);   // LOCODIR v1: clip by the real direction of travel with this character's states (row in loco_dir.json; no row / empty state / guard-walk = clip unchanged)
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
            else f.Play(clip, LocoBlendS); /* DATALAYER b10 */
            if (LocoDir.Enabled) f.anim.speed = locoDir.Speed(f.anim.speed, dt, f.anim.transform, scale);   // LOCODIR v1: Animator speed = body speed / (stride x model scale), rate limited; no row / guard-walk / no stride = speed unchanged
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
        f.anim.SetLayerWeight(guardLayer, guardLayerW * WoundSystem.GuardLayerMul(this));   // lane WOUND: the guard arm sags when hurt
    }
}












