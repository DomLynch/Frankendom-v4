using UnityEngine;

// HITPHYS v1 (lane HITPHYS): live hit physics for the HitReact partial ragdoll (partial class; data in hit_physics.json via HitPhysics.cs). PRESENTATION ONLY.
//  Hooks (HitReact.cs, marker HITPHYS v1): TriggerHit -> PhysTune (re-scales the class row for this blow) and PhysAfter (extras, stagger step); StartRow's zone push -> PushPhys; TriggerClash -> ClashMul.
//  Reads: CombatEvent (a struct copy), DuelFighter.CharacterId. Writes: ONLY HitReact's own proxy bodies / Reaction list / the bone ROTATIONS HitReact already writes. Never a DuelFighter / Fighter /
//  CharacterController field, never an Animator parameter, no UnityEngine.Random, no collider. With hit_physics.json enabled=false every hook returns its input at the first line.
//  Per blow cost: a few array scans and one Pow; the only allocation is the optional step Reaction (+2 float[18]) on a big hit, same as every HitReact Reaction. Nothing allocates per frame.
public partial class HitReact
{
    HitClassRow physCls; HpType physType; HpFighter physVictim; HitRow physRow;
    bool physPoint; Vector3 physAt; float physScore, physBase;

    /// <summary>Scale this blow's class row by its hit type, the attacker's punch, the victim's mass and class. Returns c0 unchanged when off. Sets physPoint / physAt / physScore for PushPhys and PhysAfter.</summary>
    HitClassRow PhysTune(CombatEvent e, HitRow row, HitClassRow c0, string className, Vector3 dir)
    {
        physPoint = false; physScore = 0f; physType = null; physRow = row;
        var hp = HitPhysics.Cfg; if (!hp.enabled || row == null) return c0;
        var af = HitPhysics.Fighter(e.actor != null ? e.actor.CharacterId : "");
        var vf = HitPhysics.Fighter(df != null ? df.CharacterId : "");
        var ty = HitPhysics.TypeFor(e.moveId, af); if (ty == null) return c0;
        var hc = HitPhysics.Class(className); float ci = hc != null ? hc.impulse : 1f, cb = hc != null ? hc.blend : 1f, ch = hc != null ? hc.hold : 1f;
        float ms = HitPhysics.MassScale(vf), mb = Mathf.Pow(ms, hp.blendExponent);
        var p = physCls; if (p == null) { p = new HitClassRow(); physCls = p; }   // one row per fighter, made at its first blow (not per frame)
        float im = Mathf.Min(hp.maxImpulseMul, ty.impulse * ci * af.punch * vf.reactMul * ms);
        p.name = c0.name; p.impulse = c0.impulse * im; p.twist = c0.twist * ty.twist * Mathf.Min(hp.maxImpulseMul, af.punch * vf.reactMul * ms);
        p.dip = Mathf.Clamp(c0.dip * ty.dip * mb, 0f, hp.dipMax);
        p.blend = Mathf.Clamp(ty.blend * cb * vf.blendMul * mb, hp.blendFloor, hp.blendMax);
        // absolute timing (data): ease stays the row's, hold + back come from the type; the sum is held inside [durMinS, durMaxS]
        float ease = Mathf.Max(0f, row.timing.easeInS), hold = Mathf.Max(0f, ty.holdS * ch), back = Mathf.Max(0f, ty.backS * ch);
        float tot = ease + hold + back;
        if (tot > hp.durMaxS && tot > Mathf.Epsilon) { float k = Mathf.Max(0f, hp.durMaxS - ease) / Mathf.Max(Mathf.Epsilon, hold + back); hold *= k; back *= k; }
        else if (tot < hp.durMinS) { float k = (hp.durMinS - ease) / Mathf.Max(Mathf.Epsilon, hold + back); hold *= k; back *= k; }
        p.holdS = row.timing.holdS > Mathf.Epsilon ? hold / row.timing.holdS : 1f;
        p.backS = row.timing.backS > Mathf.Epsilon ? back / row.timing.backS : 1f;
        physType = ty; physVictim = vf; physScore = im * c0.impulse * ty.stepWeight;
        physBase = row.impulse * p.impulse;
        if (ty.atPoint && e.point.sqrMagnitude > Mathf.Epsilon)
        {
            float sgn = e.direction == "left" ? -1f : (e.direction == "right" ? 1f : 0f);
            physAt = e.point; if (sgn != 0f && ty.pointLateralM != 0f) physAt += Vector3.Cross(Vector3.up, dir).normalized * (ty.pointLateralM * sgn);   // off-axis point = a torque along the swing
            physPoint = true;
        }
        return p;
    }

    /// <summary>StartRow's zone push: at the blow point (thrust = sharp linear, slash = off-axis = torque) when the type says so, else the plain impulse.</summary>
    void PushPhys(int idx, Vector3 v) { if (physPoint) PushAt(idx, v, physAt); else Push(idx, v); }

    /// <summary>After StartRow: whole-body extras (maul / claw), and a stagger step on a big hit of a fighter whose row allows it.</summary>
    void PhysAfter(CombatEvent e, Vector3 dir)
    {
        var ty = physType; if (ty == null || reactions.Count == 0) { physPoint = false; return; }
        physPoint = false;
        string dn = DirName(dir); char near = NearSide(dn);
        var last = reactions[reactions.Count - 1];
        for (int i = 0; i < ty.extra.Length; i++)
        {
            var x = ty.extra[i]; if (x == null) continue;
            int idx = ResolvePart(x.part, near); if (idx < 0) continue;
            Push(idx, dir * (physBase * x.share) + Vector3.up * (physBase * x.share * ty.extraLift * ty.lift));
            if (x.blend > 0f && last.share != null && idx < last.share.Length) last.share[idx] = Mathf.Max(last.share[idx], Mathf.Clamp(x.blend * physCls.blend / Mathf.Max(Mathf.Epsilon, ty.blend), 0f, HitPhysics.Cfg.blendMax));
        }
        var vf = physVictim;
        if (vf != null && vf.step && physScore >= vf.stepThreshold)
        {
            var r = new Reaction { tag = "step", ease = vf.stepEaseS, hold = vf.stepDurS * vf.stepHoldFrac, back = vf.stepDurS * (1f - vf.stepHoldFrac), dipMul = 1f, blendMul = 1f };
            r.dip = new float[n]; r.share = new float[n];
            for (int i = 0; i < n; i++) r.dip[i] = 1f;
            int th = ResolvePart(vf.stepPart, near), sh = ResolvePart(vf.stepShin, near), ft = ResolvePart(vf.stepFoot, near);
            float b = Mathf.Clamp(vf.stepBlend, 0f, HitPhysics.Cfg.blendMax);
            if (th >= 0) { r.dip[th] = vf.stepThighDip; r.share[th] = b; }
            if (sh >= 0) { r.dip[sh] = vf.stepShinDip; r.share[sh] = b * vf.stepShinShare; }
            if (ft >= 0) { r.dip[ft] = vf.stepFootDip; r.share[ft] = b * vf.stepFootShare; }
            AddReaction(r);
            if (th >= 0) Push(th, dir * (vf.stepImpulse * Mathf.Min(physScore, HitPhysics.Cfg.maxImpulseMul)) + Vector3.up * vf.stepLift);   // the far leg is thrown out along the blow = a recovery step
        }
    }

    /// <summary>Block / parry / broken guard: scale the arm recoil by kind, the fighter's own row and mass (attacker: its swing weight; defender: its mass and the attacker's punch).</summary>
    float ClashMul(string kind, bool attacker, CombatEvent e)
    {
        var hp = HitPhysics.Cfg; if (!hp.enabled) return 1f;
        var me = HitPhysics.Fighter(df != null ? df.CharacterId : "");
        float m = HitPhysics.ClashKind(kind) * me.clashMul * HitPhysics.MassScale(me);
        if (!attacker) { var other = HitPhysics.Fighter(e.actor != null ? e.actor.CharacterId : ""); m *= other.punch; }
        return m;
    }
}
