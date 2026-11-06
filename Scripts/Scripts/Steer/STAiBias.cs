using UnityEngine;

// AI bias: stance ops, then the running tactic's ops, applied on top of the player's base AiProfile (the playerBot row) and written into AIBrain.profile in place every frame.
// AIBrain.Decide reads the profile on every sim tick, so a stance change takes effect on the next decision (<= 1 tick, well inside the 0.5 s spec).
public static class STAiBias
{
    public static AIBrain Brain;
    static AiProfile baseP, boundTo;
    static float[] v = new float[17];
    public static readonly string[] Knobs = { "aggression", "pressure", "parry", "dodge", "lapse", "guard", "circle", "feint", "step", "kick", "dash", "disengage", "interrupt", "discipline", "read", "reaction", "accuracy" };
    public static bool Ready { get { return baseP != null && Brain != null; } }

    static float Get(AiProfile p, int i)
    {
        switch (i)
        {
            case 0: return p.aggression; case 1: return p.pressure; case 2: return p.parry; case 3: return p.dodge; case 4: return p.lapse; case 5: return p.guard;
            case 6: return p.circle; case 7: return p.feint; case 8: return p.step; case 9: return p.kick; case 10: return p.dash; case 11: return p.disengage;
            case 12: return p.interrupt; case 13: return p.discipline; case 14: return p.read; case 15: return p.reaction; default: return p.accuracy;
        }
    }
    static void Set(AiProfile p, int i, float x)
    {
        switch (i)
        {
            case 0: p.aggression = x; break; case 1: p.pressure = x; break; case 2: p.parry = x; break; case 3: p.dodge = x; break; case 4: p.lapse = x; break; case 5: p.guard = x; break;
            case 6: p.circle = x; break; case 7: p.feint = x; break; case 8: p.step = x; break; case 9: p.kick = x; break; case 10: p.dash = x; break; case 11: p.disengage = x; break;
            case 12: p.interrupt = x; break; case 13: p.discipline = x; break; case 14: p.read = x; break; case 15: p.reaction = x; break; default: p.accuracy = x; break;
        }
    }
    static STOp Op(STOps o, int i)
    {
        switch (i)
        {
            case 0: return o.aggression; case 1: return o.pressure; case 2: return o.parry; case 3: return o.dodge; case 4: return o.lapse; case 5: return o.guard;
            case 6: return o.circle; case 7: return o.feint; case 8: return o.step; case 9: return o.kick; case 10: return o.dash; case 11: return o.disengage;
            case 12: return o.interrupt; case 13: return o.discipline; case 14: return o.read; case 15: return o.reaction; default: return o.accuracy;
        }
    }

    public static void Bind(AIBrain b) { Brain = b; boundTo = null; baseP = null; }

    static void Layer(STOps o)
    {
        if (o == null) return;
        for (int i = 0; i < v.Length; i++)
        {
            var op = Op(o, i); if (op == null) continue;
            if (op.set == 0f && op.mul == 0f && op.add == 0f) continue;   // all-zero = an op the deserializer left blank (identity)
            float x = v[i];
            if (!float.IsNaN(op.set)) x = op.set;
            v[i] = x * op.mul + op.add;
        }
    }

    // stance + (optional) tactic -> brain.profile
    public static void Refresh(STCfg cfg, STStanceDef stance, STTacticDef tactic)
    {
        if (Brain == null || Brain.profile == null) return;
        if (Brain.profile != boundTo) { baseP = Brain.profile.Clone(); boundTo = Brain.profile; }   // AIBrain.Init may swap the profile object: re-snapshot the fresh base
        for (int i = 0; i < v.Length; i++) v[i] = Get(baseP, i);
        if (stance != null) Layer(stance.profile);
        if (tactic != null) Layer(tactic.profile);
        var cl = cfg != null && cfg.clamp != null ? cfg.clamp.list : null;
        if (cl != null)
            for (int c = 0; c < cl.Length; c++)
            {
                if (cl[c] == null || string.IsNullOrEmpty(cl[c].knob)) continue;
                int i = System.Array.IndexOf(Knobs, cl[c].knob);
                if (i >= 0) v[i] = Mathf.Clamp(v[i], cl[c].lo, cl[c].hi);
            }
        for (int i = 0; i < v.Length; i++) Set(boundTo, i, v[i]);
    }
}
