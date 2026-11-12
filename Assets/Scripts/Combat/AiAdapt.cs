using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// V63 enemy-AI layer: combos, whiff punishment, timing mixups, enrage, player-habit adaptation, riposte spread, kick cap, move-share cap.
// Every number lives in Assets/Data/ai_adapt.json (code holds only the mechanics). The layer is null-safe: AIBrain hooks do nothing when
// AiAdapt.Create returns null (file missing, "enabled": false, or no profile row matches the fighter). It draws only from its own seeded LCG,
// so the AIBrain random stream is untouched. No telegraphs, holds, glows or sounds: it only changes WHICH natural action is chosen and WHEN.

[Serializable] public class AdaptFx { public float parry, dodge, aggression, pressure, kick, interrupt, feint, stab, read, lapse, reaction, dash, circle; }
[Serializable] public class AdaptCombo { public string id; public string[] steps = new string[0]; public float weight = 1f; }
[Serializable]
public class AdaptResponse
{
    public string id, trigger;                 // trigger: lightSpam | rollSame | turtle
    public float minCount = 4, share = 0.7f;    // lightSpam/rollSame: events inside the memory window; share of them that match. turtle: share of observed ticks guarding
    public float rampTicks = 120, holdTicks = 600;
    public bool cutOff;                         // rollSame: step toward the side the player keeps rolling to
    public AdaptFx fx = new AdaptFx();
}
[Serializable]
public class AdaptEnrage
{
    public bool enabled; public float hpFrac = 0.5f, speedMul = 1f, waitMul = 1f, reactionMul = 1f, comboAdd, punishAdd, diversify;
    public AdaptFx fx = new AdaptFx();
}
[Serializable] public class AdaptAlt { public string from; public string[] to = new string[0]; }
[Serializable]
public class AdaptProfile
{
    public string id, fighter = ""; public int minDiff = 0, maxDiff = 99; public bool bot;
    public float comboChance; public AdaptCombo[] combos = new AdaptCombo[0];
    public float punishChance, punishReact = 4, punishMinRemain = 6, punishApproachMin = 10, punishDashGap = 1.6f, punishHeavyRemain = 34;
    public float delayChance, delayMin = 6, delayMax = 20, earlyChance, earlyMul = 0.6f, lateChance, lateMul = 1.5f;
    public float riposteTake = 1f;
    public int kickMax = 99, kickWindow = 1800, kickGap = 0;
    public float maxShare = 1f, lightMaxShare = 1f; public int minSample = 8;
    public AdaptAlt[] alts = new AdaptAlt[0];
    public AdaptEnrage enrage = new AdaptEnrage();
}
[Serializable]
public class AdaptRiposte
{
    public bool consumeAi = true, consumePlayer, variantsAi = true, variantsPlayer;
    public string lightMove = "slash_riposte", heavyMove = "riposte", thrustMove = "riposte";
    public float pressLight = 0.45f, pressHeavy = 0.2f, pressThrust = 0.35f;
}
[Serializable]
public class AdaptFile
{
    public bool enabled = true; public int memoryTicks = 1800;
    public AdaptRiposte riposte = new AdaptRiposte();
    public AdaptResponse[] responses = new AdaptResponse[0];
    public AdaptProfile[] profiles = new AdaptProfile[0];
}

public class AiAdapt
{
    // ---------------- data ----------------
    static AdaptFile cfg; static bool loaded;
    public static AdaptFile Cfg()
    {
        if (loaded) return cfg;
        loaded = true; var f = new AdaptFile(); string text = null;
        try
        {
            string[] paths = { Path.Combine(Application.dataPath, "Data/ai_adapt.json"), Path.Combine(Application.streamingAssetsPath, "ai_adapt.json") };
            for (int i = 0; i < paths.Length && text == null; i++) if (File.Exists(paths[i])) text = DataPath.ReadFile(paths[i]);
            if (text == null) { var ta = Resources.Load<TextAsset>("ai_adapt"); if (ta != null) text = ta.text; }
        }
        catch (Exception e) { Debug.LogWarning("[AiAdapt] read failed: " + e.Message); }
        if (text == null) { cfg = null; return null; }
        try { JsonUtility.FromJsonOverwrite(text, f); } catch (Exception e) { Debug.LogWarning("[AiAdapt] parse failed: " + e.Message); cfg = null; return null; }
        cfg = f.enabled ? f : null; return cfg;
    }
    public static void Reload() { loaded = false; cfg = null; }

    // ---------------- DuelFighter hook: the move a parry riposte plays ----------------
    public static string RiposteMove(DuelFighter f, bool light, bool heavy, bool thrust, ref int riposteTicks)
    {
        var C = Cfg(); string mv = "riposte";
        if (C == null || f == null) return mv;
        bool pl = f.IsPlayer;
        if (pl ? C.riposte.variantsPlayer : C.riposte.variantsAi)
        {
            string want = heavy ? C.riposte.heavyMove : light ? C.riposte.lightMove : C.riposte.thrustMove;
            if (!string.IsNullOrEmpty(want) && f.moves != null && f.moves.Get(want) != null) mv = want;
        }
        if (pl ? C.riposte.consumePlayer : C.riposte.consumeAi) riposteTicks = 0;   // one riposte per parry
        return mv;
    }

    public static AiAdapt Of(DuelFighter f) { if (f == null) return null; var b = f.GetComponent<AIBrain>(); return b != null ? b.adapt : null; }

    // ---------------- instance ----------------
    readonly AIBrain b; readonly AdaptFile C; readonly AdaptProfile P; readonly AiProfile baseP;
    uint seed; float baseWalk, baseRun;
    public bool enraged; public int enrageTick = -1;
    // memory
    readonly Queue<int> lightQ = new Queue<int>(), otherQ = new Queue<int>(), rollT = new Queue<int>(), rollC = new Queue<int>();
    readonly int[] gGuard, gTot, gStamp; const int Bucket = 30;
    int lastTickSeen = -1;
    readonly float[] strength; readonly int[] lastTrig; readonly int[] act;
    AdaptFx fx = new AdaptFx(); float comboAdd, punishAdd, waitMul = 1f;
    int cutSide;
    // own-move counters
    readonly Dictionary<string, int> cnt = new Dictionary<string, int>(); int total;
    readonly Queue<int> kickT = new Queue<int>(); int lastKick = -100000;
    // combo / timing / whiff / riposte
    string[] plan; int planIdx;
    int holdUntil; bool afterHold;
    int whiffSerial = -1; bool whiffTake, whiffNow; int whiffRemain;
    int ripSeen = -1; bool ripTake = true;
    // report counters
    public int nCombos, nComboSteps, nWhiff, nDelay, nRipTaken, nRipSkipped, nKickSwap, nShareSwap, nDiversify;

    public static AiAdapt Create(AIBrain brain)
    {
        var C = Cfg(); if (C == null || brain == null || brain.df == null) return null;
        AdaptProfile pick = null; string key = RosterResolver.IdOfName(brain.name);   // HCKIT2 v93
        for (int i = 0; i < C.profiles.Length && pick == null; i++)
        {
            var r = C.profiles[i]; if (r == null) continue;
            if (r.fighter.Length > 0 && !RosterResolver.KeyIs(r.fighter, key)) continue;   // HCKIT2 v93 (id, @player, @enemy, *; "" = any)
            if (brain.diff == null) { if (r.bot) pick = r; }
            else if (!r.bot && brain.diff.id >= r.minDiff && brain.diff.id <= r.maxDiff) pick = r;
        }
        Debug.Log("[AiAdapt] " + brain.name + " key " + key + " -> profile " + (pick == null ? "(none)" : pick.id));   // dev2: which profile row won, per fighter (first match wins)
        return pick == null ? null : new AiAdapt(brain, C, pick);
    }

    AiAdapt(AIBrain brain, AdaptFile c, AdaptProfile p)
    {
        b = brain; C = c; P = p; baseP = brain.profile.Clone();
        seed = unchecked(0x9E3779B1u ^ (uint)brain.name.GetHashCode() ^ ((uint)AIBrain.GlobalSeed * 2654435761u) ^ (uint)brain.seedOffset);
        int nb = Mathf.Max(4, C.memoryTicks / Bucket + 2); gGuard = new int[nb]; gTot = new int[nb]; gStamp = new int[nb];
        for (int i = 0; i < nb; i++) gStamp[i] = -1;
        strength = new float[C.responses.Length]; lastTrig = new int[C.responses.Length]; act = new int[C.responses.Length];
        for (int i = 0; i < lastTrig.Length; i++) lastTrig[i] = -100000;
        baseWalk = brain.df.walkSpeed; baseRun = brain.df.runSpeed;
    }

    public void Release()
    {
        if (b != null && b.df != null && enraged) { b.df.walkSpeed = baseWalk; b.df.runSpeed = baseRun; }
        enraged = false;
    }

    float Roll() { seed = unchecked(seed * 1664525u + 1013904223u); return (float)(seed / 4294967296.0); }
    float Range(float a, float c) { return a + (c - a) * Roll(); }

    static string Cat(string id)
    {
        if (id == null) return "other";
        if (id == "riposte" || id == "slash_riposte" || id == "heavy_riposte" || id == "heavy_counter") return "riposte";
        if (id.StartsWith("light")) return "light";
        if (id.StartsWith("heavy")) return "heavy";
        if (id == "thrust") return "thrust";
        if (id == "kick") return "kick";
        if (id.StartsWith("skill_")) return "special";
        return "other";
    }

    // ---------------- notifications ----------------
    public void OnOwnAttack(MoveDef m)
    {
        string c = Cat(m.id); int v; cnt.TryGetValue(c, out v); cnt[c] = v + 1; if (c != "special") total++;
        int tick = Duel.TickCount;
        if (c == "kick") { kickT.Enqueue(tick); lastKick = tick; }
        if (c == "riposte") AiWorld.Track(b.df).riposteUntil = 0;   // the parry window is spent
    }
    public void OnFoeAttack(MoveDef m, bool punishes)
    {
        int tick = Duel.TickCount;
        if ((m.id == "light_left" || m.id == "light_right") && !punishes) lightQ.Enqueue(tick); else otherQ.Enqueue(tick);
    }
    public void OnFoeAction(string a)
    {
        if (a != "roll") return;
        var o = b.df.opponent; if (o == null) return;
        Vector3 to = o.transform.position - b.transform.position; to.y = 0f; if (to.sqrMagnitude < 1e-4f) return;
        Vector3 dir = to.normalized, lat = new Vector3(dir.z, 0f, -dir.x), mv = o.moveDir;
        int cat = 3;
        if (mv.sqrMagnitude > 0.01f) { float ax = Vector3.Dot(mv, lat), az = Vector3.Dot(mv, dir); cat = Mathf.Abs(ax) >= Mathf.Abs(az) ? (ax > 0f ? 0 : 1) : (az > 0f ? 2 : 3); }
        rollT.Enqueue(Duel.TickCount); rollC.Enqueue(cat);
    }

    // ---------------- per decision ----------------
    static void Prune(Queue<int> q, int min) { while (q.Count > 0 && q.Peek() < min) q.Dequeue(); }

    public void Tick(int n, bool foeGuarding, int tick)
    {
        if (tick == lastTickSeen) return;
        lastTickSeen = tick;
        int mem = C.memoryTicks, min = tick - mem;
        Prune(lightQ, min); Prune(otherQ, min); Prune(kickT, tick - P.kickWindow);
        while (rollT.Count > 0 && rollT.Peek() < min) { rollT.Dequeue(); rollC.Dequeue(); }
        int stamp = tick / Bucket, bi = stamp % gStamp.Length;
        if (gStamp[bi] != stamp) { gStamp[bi] = stamp; gGuard[bi] = 0; gTot[bi] = 0; }
        gTot[bi] += Mathf.Max(1, n); if (foeGuarding) gGuard[bi] += Mathf.Max(1, n);

        // habits -> responses
        int lights = lightQ.Count, others = otherQ.Count;
        int gT = 0, gG = 0; for (int i = 0; i < gStamp.Length; i++) if (gStamp[i] >= stamp - mem / Bucket) { gT += gTot[i]; gG += gGuard[i]; }
        int[] rc = new int[4]; foreach (int c in rollC) rc[c]++;
        int rolls = rollC.Count, domCat = 0; for (int i = 1; i < 4; i++) if (rc[i] > rc[domCat]) domCat = i;
        fx = new AdaptFx(); cutSide = 0;
        for (int i = 0; i < C.responses.Length; i++)
        {
            var R = C.responses[i]; if (R == null) continue;
            bool on = false;
            if (R.trigger == "lightSpam") on = lights >= R.minCount && lights / (float)Mathf.Max(1, lights + others) >= R.share;
            else if (R.trigger == "rollSame") on = rolls >= R.minCount && rc[domCat] / (float)rolls >= R.share;
            else if (R.trigger == "turtle") on = gT >= mem * 0.25f && gG / (float)gT >= R.share;
            float before = strength[i];
            if (on) { lastTrig[i] = tick; strength[i] = Mathf.Min(1f, strength[i] + n / Mathf.Max(1f, R.rampTicks)); }
            else if (tick > lastTrig[i] + R.holdTicks) strength[i] = Mathf.Max(0f, strength[i] - n / Mathf.Max(1f, R.rampTicks));
            if (before <= 0f && strength[i] > 0f) act[i]++;
            float k = strength[i]; if (k <= 0f) continue;
            Add(ref fx, R.fx, k);
            if (R.cutOff && R.trigger == "rollSame" && k > 0.5f && (domCat == 0 || domCat == 1)) cutSide = domCat == 0 ? 1 : -1;
        }

        // enrage
        comboAdd = 0f; punishAdd = 0f; waitMul = 1f; float reactionMul = 1f;
        var E = P.enrage; var df = b.df;
        if (E.enabled)
        {
            float frac = df.maxHealth > 0f ? df.health / df.maxHealth : 1f;
            if (!enraged && df.health > 0f && frac <= E.hpFrac) { enraged = true; enrageTick = tick; df.walkSpeed = baseWalk * E.speedMul; df.runSpeed = baseRun * E.speedMul; }
            else if (enraged && frac > E.hpFrac + 0.25f) { enraged = false; df.walkSpeed = baseWalk; df.runSpeed = baseRun; }
        }
        if (enraged) { Add(ref fx, E.fx, 1f); comboAdd = E.comboAdd; punishAdd = E.punishAdd; waitMul = E.waitMul; reactionMul = E.reactionMul; }

        // effective profile = base + effects (in place; AIBrain holds the same object)
        var p = b.profile;
        p.parry = Mathf.Clamp01(baseP.parry + fx.parry); p.dodge = Mathf.Clamp01(baseP.dodge + fx.dodge);
        p.aggression = Mathf.Clamp(baseP.aggression + fx.aggression, 0f, 1.5f); p.pressure = Mathf.Clamp01(baseP.pressure + fx.pressure);
        p.kick = Mathf.Clamp01(baseP.kick + fx.kick); p.interrupt = Mathf.Clamp01(baseP.interrupt + fx.interrupt); p.feint = Mathf.Clamp01(baseP.feint + fx.feint);
        p.stab = Mathf.Clamp01(baseP.stab + fx.stab); p.dash = Mathf.Clamp01(baseP.dash + fx.dash); p.circle = Mathf.Clamp01(baseP.circle + fx.circle);
        p.lapse = Mathf.Clamp01(baseP.lapse + fx.lapse); p.read = baseP.read < 0f && fx.read == 0f ? -1f : Mathf.Clamp01(Mathf.Max(0f, baseP.read) + fx.read);
        p.reaction = Mathf.Max(4f, (baseP.reaction + fx.reaction) * reactionMul);
        if (cutSide != 0) b.ai.side = cutSide;
    }
    static void Add(ref AdaptFx a, AdaptFx x, float k)
    {
        a.parry += x.parry * k; a.dodge += x.dodge * k; a.aggression += x.aggression * k; a.pressure += x.pressure * k; a.kick += x.kick * k; a.interrupt += x.interrupt * k;
        a.feint += x.feint * k; a.stab += x.stab * k; a.read += x.read * k; a.lapse += x.lapse * k; a.reaction += x.reaction * k; a.dash += x.dash * k; a.circle += x.circle * k;
    }

    // whiff punishment: the foe swung, nothing landed, and it is in recovery
    public bool Opening(bool opening)
    {
        whiffNow = false;
        float chance = P.punishChance + punishAdd; if (chance <= 0f) return opening;
        var o = b.df.opponent; if (o == null || o.state != FState.Recovery || o.move == null) return opening;
        var ot = AiWorld.Track(o); if (ot.landed) return opening;
        if (ot.serial != whiffSerial) { whiffSerial = ot.serial; whiffTake = Roll() < chance; if (whiffTake) nWhiff++; }
        if (!whiffTake) return opening;
        whiffRemain = o.WeaponTicks(o.move.Recovery(ot.chained)) /* WEAPONSWAP v88 */ - o.t;
        if (whiffRemain >= P.punishMinRemain && o.t >= P.punishReact) { whiffNow = true; return true; }
        return opening;
    }
    public bool WantApproach(float gap) { return whiffNow && whiffRemain >= P.punishApproachMin && gap > 0.9f; }
    public bool WantDash(float gap) { return whiffNow && whiffRemain >= P.punishApproachMin && gap > P.punishDashGap; }

    // runs first inside the offence block. null = nothing to say, "" = hold all offence this decision, else the combo follow-up to press
    public string Pre(bool chainOpen, bool canAct, float gap, MoveDef mLight, MoveDef mHeavy, MoveDef mThrust)
    {
        int tick = Duel.TickCount; var st = AiWorld.Track(b.df);
        // riposte choice: decided once per parry
        if (tick < st.riposteUntil)
        {
            if (st.riposteUntil != ripSeen) { ripSeen = st.riposteUntil; ripTake = Roll() < P.riposteTake; if (ripTake) nRipTaken++; else nRipSkipped++; }
            if (!ripTake) return "";
        }
        if (planIdx > 0 && plan != null)
        {
            if (canAct && !chainOpen) { plan = null; planIdx = 0; return null; }
            if (chainOpen && planIdx < plan.Length)
            {
                string step = plan[planIdx]; var m = b.df.move; bool ok = false;
                if (m != null && m.chainFollow != null)
                {
                    if (step == "heavy") ok = System.Array.IndexOf(m.chainFollow, "heavy_overhead") >= 0;
                    else if (step == "thrust") ok = System.Array.IndexOf(m.chainFollow, "thrust") >= 0;
                    else ok = System.Array.IndexOf(m.chainFollow, "light_left") >= 0 || System.Array.IndexOf(m.chainFollow, "light_right") >= 0;
                }
                MoveDef rm = step == "heavy" ? mHeavy : step == "thrust" ? mThrust : mLight;
                if (ok && rm != null && gap <= rm.reach - 0.1f) { planIdx++; nComboSteps++; if (planIdx >= plan.Length) { plan = null; planIdx = 0; } return step; }
                plan = null; planIdx = 0;
            }
        }
        return null;
    }

    // the chosen offence action goes through here; null = do not act this decision (timing hesitation)
    public string Filter(string best, string action, bool rL, bool rH, bool rT, bool rK)
    {
        int tick = Duel.TickCount;
        bool urgent = best == "deathblow" || best == "punish" || best == "counter" || best == "critical";
        if (best == "counter")
        {
            var R = C.riposte; float tot = Mathf.Max(0.0001f, R.pressLight + R.pressHeavy + R.pressThrust), r = Roll() * tot;
            action = r < R.pressLight && rL ? "light" : r < R.pressLight + R.pressHeavy ? "heavy" : rT || !rL ? "thrust" : "light";
            if (action == "thrust" && !rT) action = "heavy";
        }
        if (whiffNow && best == "punish" && rH && whiffRemain >= P.punishHeavyRemain) action = "heavy";
        if (!urgent)
        {
            if (holdUntil > tick) return null;
            if (holdUntil > 0) { holdUntil = 0; afterHold = true; }
            else if (!afterHold && P.delayChance > 0f && Roll() < P.delayChance) { holdUntil = tick + Mathf.RoundToInt(Range(P.delayMin, P.delayMax)); nDelay++; return null; }
        }
        afterHold = false;
        if (!urgent)
        {
            string cat = action;
            if (action == "kick" && KickBlocked(tick)) { string alt = Sub("kick", rL, rH, rT, false, tick); if (alt != action) { nKickSwap++; action = alt; } }
            else if (ShareFull(cat)) { string alt = Sub(cat, rL, rH, rT, rK, tick); if (alt != action) { nShareSwap++; action = alt; } }
            if (enraged && P.enrage.diversify > 0f && Roll() < P.enrage.diversify)
            {
                string least = action; int lv = int.MaxValue;
                string[] cs = { "light", "heavy", "thrust", "kick" }; bool[] rr = { rL, rH, rT, rK && !KickBlocked(tick) };
                for (int i = 0; i < cs.Length; i++) { int v; cnt.TryGetValue(cs[i], out v); if (rr[i] && v < lv) { lv = v; least = cs[i]; } }
                if (least != action) { nDiversify++; action = least; }
            }
        }
        // combo start
        if (plan == null && (action == "light" || action == "heavy") && P.combos.Length > 0)
        {
            float cc = P.comboChance + comboAdd;
            if (cc > 0f && Roll() < cc)
            {
                float wsum = 0f; for (int i = 0; i < P.combos.Length; i++) if (P.combos[i].steps.Length > 1 && P.combos[i].steps[0] == action) wsum += P.combos[i].weight;
                if (wsum > 0f)
                {
                    float x = Roll() * wsum;
                    for (int i = 0; i < P.combos.Length; i++)
                    {
                        var cb = P.combos[i]; if (cb.steps.Length < 2 || cb.steps[0] != action) continue;
                        x -= cb.weight; if (x <= 0f) { plan = cb.steps; planIdx = 1; nCombos++; break; }
                    }
                }
            }
        }
        return action;
    }

    public float Wait(float w)
    {
        w *= waitMul;
        float r = Roll();
        if (r < P.earlyChance) w *= P.earlyMul; else if (r < P.earlyChance + P.lateChance) w *= P.lateMul;
        return Mathf.Max(0f, Mathf.Round(w));
    }

    // ---------------- caps ----------------
    bool KickBlocked(int tick)
    {
        if (tick - lastKick < P.kickGap) return true;
        return kickT.Count >= P.kickMax;
    }
    bool ShareFull(string cat)
    {
        if (total < P.minSample || cat == "kick") return false;
        int v; cnt.TryGetValue(cat, out v);
        float cap = cat == "light" ? P.lightMaxShare : P.maxShare;
        return v / (float)total >= cap;
    }
    string Sub(string from, bool rL, bool rH, bool rT, bool rK, int tick)
    {
        for (int i = 0; i < P.alts.Length; i++)
        {
            if (P.alts[i] == null || P.alts[i].from != from) continue;
            foreach (string c in P.alts[i].to)
            {
                bool ok = c == "light" ? rL : c == "heavy" ? rH : c == "thrust" ? rT : c == "kick" ? rK && !KickBlocked(tick) : false;
                if (ok && !ShareFull(c)) return c;
            }
        }
        return from;
    }

    // ---------------- report (BalanceAuto) ----------------
    public float EnrageSeconds(float ticksPerSecond) { return enrageTick < 0 ? -1f : enrageTick / Mathf.Max(1f, ticksPerSecond); }
    public string Report()
    {
        var sb = new StringBuilder();
        sb.Append("enrage ").Append(enrageTick >= 0 ? "yes@" + (enrageTick / 60f).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "s" : "no");
        sb.Append(" combos ").Append(nCombos).Append("/").Append(nComboSteps).Append(" whiffPunish ").Append(nWhiff).Append(" delays ").Append(nDelay);
        sb.Append(" riposte take/skip ").Append(nRipTaken).Append("/").Append(nRipSkipped).Append(" kickSwap ").Append(nKickSwap).Append(" shareSwap ").Append(nShareSwap).Append(" diversify ").Append(nDiversify);
        sb.Append(" adapt");
        for (int i = 0; i < C.responses.Length; i++) if (C.responses[i] != null) sb.Append(' ').Append(C.responses[i].id).Append('x').Append(act[i]);
        return sb.ToString();
    }
}
