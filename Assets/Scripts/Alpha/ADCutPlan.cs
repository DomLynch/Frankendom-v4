using System;
using System.Collections.Generic;

// ALPHA lane B: finisher cut PLAN (pure C#, port of Armagedom engine/finisher-cuts.js + data/pitbornCuts.json) and the gore/cut data shapes.
// Presentation only: which cut finisher a kill rolls (seeded, no repeat, pity rule), and which units are cut at which second after the killing blow.
// The mesh work is ADMeshCut (Unity). Data lives in duel_rules.json -> wounds.gore / wounds.cuts.

[Serializable] public class ADSprayCfg { public float seconds = 1.0f, rate = 90f, coneDeg = 16f, gravity = 8f; public float[] speed = { 2.5f, 4.5f }, size = { 0.03f, 0.06f }, life = { 0.5f, 0.9f }; }
[Serializable] public class ADGoreLoc { public string name = "torso"; public string[] bones = new string[0]; public float radiusM = 0.12f; public float frontArcDeg = 360f; public float[] along = { 0.2f, 0.8f }; }
[Serializable] public class ADGoreCfg
{
    public bool enabled = true;
    public int maxDecalsPerLoc = 14, seed = 99, texSize = 64;
    public int[] decalsBySeverity = { 0, 3, 6, 10 };   // decals on a location when its severity rises to index
    public float critMul = 1.6f;
    public float[] sizeM = { 0.07f, 0.16f };           // decal quad edge, min..max, metres
    public float[] color = { 0.30f, 0.02f, 0.02f, 1f };
    public float quadLift = 0.004f;
    public ADGoreLoc[] locs = new ADGoreLoc[0];
    public ADSprayCfg woundSpray = new ADSprayCfg { seconds = 0.35f, rate = 60f };
    public ADSprayCfg neckSpray = new ADSprayCfg();
}

[Serializable] public class ADCutFinisher { public string id = ""; public string unit = ""; public string[] also = new string[0]; public float atS, alsoAtS, push = 1f, pieceImpulse = 60f; public string fallback = ""; }
[Serializable] public class ADCutPick { public string id = ""; public float weight = 1f; }
[Serializable] public class ADCutBinding { public string[] weapons = new string[0]; public ADCutPick[] pool = new ADCutPick[0]; }
[Serializable]
public class ADCutUnit
{
    public string id = "";
    public string kind = "subtree";          // subtree: detach everything skinned to the bone subtree; split: split the subtree's mesh down a plane, halves stay attached; wound: no cut, blood only
    public string[] bones = new string[0];   // root bone of the cut (first one found)
    public string[] parentBones = new string[0];   // bone the piece hangs from (jet emitter + stump); "" = the bone's own parent
    public int loc = 3;                      // ADLoc of the wound that the gore shows
    public float massKg = 5f, colliderRadiusM = 0.1f, openDeg = 14f, splitOffsetM = 0f;
    public string splitAxis = "x";           // bone-local axis the split plane is perpendicular to
    public float jetSeconds = 1.0f;
}
[Serializable]
public class ADCutsCfg
{
    public bool enabled = true;
    public int seed = 4101, pityAfter = 2;
    public float chance = 0.9f, weightMin = 0.5f, pieceImpulseScale = 0.2f, weldM = 0.0002f;
    public bool noRepeat = true;
    public float[] capColor = { 0.435f, 0.114f, 0.114f, 1f };   // flesh (Armagedom capTint #6f1d1d)
    public float[] innerColor = { 0.165f, 0.043f, 0.043f, 1f };
    public ADCutFinisher[] finishers = new ADCutFinisher[0];
    public ADCutBinding[] bindings = new ADCutBinding[0];
    public ADCutUnit[] units = new ADCutUnit[0];
}

public struct ADCutStep { public string unit; public float atS; }
public class ADCutPlanResult { public string id; public ADCutFinisher row; public List<ADCutStep> steps = new List<ADCutStep>(); }

/// <summary>Seeded, history-aware finisher pick. One instance per fight (Reset on rematch). Same victim + weapon + history => same pick.</summary>
public class ADCutPicker
{
    public string last; public int misses, ordinal;
    public void Reset() { last = null; misses = 0; ordinal = 0; }

    public static float Draw(int seed, int victimId, int ordinal, int salt)
    {
        unchecked
        {
            ulong x = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + (ulong)(uint)victimId * 0xBF58476D1CE4E5B9UL + (ulong)(uint)ordinal * 0x94D049BB133111EBUL + (ulong)(uint)salt * 0x2545F4914F6CDD1DUL;
            x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 27; x *= 0x94D049BB133111EBUL; x ^= x >> 31;
            return (float)((x >> 40) / 16777216.0);
        }
    }

    ADCutBinding Binding(ADCutsCfg c, string weapon)
    {
        ADCutBinding any = null;
        for (int i = 0; i < c.bindings.Length; i++)
        {
            var b = c.bindings[i]; if (b == null || b.weapons == null) continue;
            for (int w = 0; w < b.weapons.Length; w++) { if (b.weapons[w] == weapon) return b; if (b.weapons[w] == "*") any = b; }
        }
        return any;
    }

    /// <summary>The finisher id this kill rolls, or null (plain death). Port of pickCutFinisher.</summary>
    public string Pick(ADCutsCfg c, string weapon, int victimId)
    {
        if (c == null || !c.enabled) return null;
        var b = Binding(c, weapon); if (b == null || b.pool.Length == 0) return null;
        int ord = ordinal++;
        if (misses < c.pityAfter && Draw(c.seed, victimId, ord, 1) >= c.chance) { misses++; return null; }
        misses = 0;
        var pool = new List<ADCutPick>();
        for (int i = 0; i < b.pool.Length; i++) if (!c.noRepeat || b.pool[i].id != last) pool.Add(b.pool[i]);
        if (pool.Count == 0) pool.AddRange(b.pool);
        float sum = 0f; for (int i = 0; i < pool.Count; i++) sum += pool[i].weight;
        float w2 = Draw(c.seed, victimId, ord, 2) * sum;
        var pick = pool[pool.Count - 1];
        for (int i = 0; i < pool.Count; i++) { w2 -= pool[i].weight; if (w2 < 0f) { pick = pool[i]; break; } }
        last = pick.id;
        return pick.id;
    }

    /// <summary>Steps of a finisher on this body: the unit (or the fallback if the body lacks it) at atS, then each "also" unit the body has at atS + alsoAtS. Port of finisherSteps.</summary>
    public static List<ADCutStep> Steps(ADCutFinisher row, Func<string, bool> has)
    {
        var steps = new List<ADCutStep>();
        string first = has(row.unit) ? row.unit : (string.IsNullOrEmpty(row.fallback) ? null : row.fallback);
        if (first != null) steps.Add(new ADCutStep { unit = first, atS = row.atS });
        for (int i = 0; i < row.also.Length; i++) if (has(row.also[i]) && row.also[i] != first) steps.Add(new ADCutStep { unit = row.also[i], atS = row.atS + row.alsoAtS });
        return steps;
    }

    /// <summary>Plan for one kill, or null (plain death). Port of planCut.</summary>
    public ADCutPlanResult Plan(ADCutsCfg c, string weapon, int victimId, Func<string, bool> has)
    {
        string id = Pick(c, weapon, victimId); if (id == null) return null;
        ADCutFinisher row = null;
        for (int i = 0; i < c.finishers.Length; i++) if (c.finishers[i] != null && c.finishers[i].id == id) { row = c.finishers[i]; break; }
        if (row == null) return null;
        var steps = Steps(row, has);
        if (steps.Count == 0) return null;
        var r = new ADCutPlanResult { id = id, row = row }; r.steps = steps; return r;
    }
}

public static class ADWoundsSelfTest
{
    /// <summary>Engine-free checks. Returns "" on success or the list of failures.</summary>
    public static string Run()
    {
        var f = new System.Text.StringBuilder();
        var c = new ADWoundsCfg();
        var s = new ADWoundSet(c);
        if (s.HeavyDamageMul() != 1f || s.SpeedTierPenalty() != 0f) f.AppendLine("fresh set not neutral");
        if (ADWoundRules.RollLocation(c, "heavy", () => 0f) != ADWoundRules.Arm) f.AppendLine("roll 0 != arm");
        if (ADWoundRules.RollLocation(c, "heavy", () => 0.999f) != ADWoundRules.Head) f.AppendLine("roll 0.999 != head");
        if (ADWoundRules.RollLocation(c, "kick", () => 0.5f) != ADWoundRules.Leg) f.AppendLine("kick 0.5 should be leg");
        if (ADWoundRules.RollLocation(c, "unknown", () => 0.5f) != ADWoundRules.Torso) f.AppendLine("fallback row 0.5 should be torso");
        var rnd = new Random(1); int[] n = new int[4];
        for (int i = 0; i < 20000; i++) n[ADWoundRules.RollLocation(c, "stab", () => (float)rnd.NextDouble())]++;
        double tf = n[2] / 20000.0; if (Math.Abs(tf - 3.0 / 5.5) > 0.02) f.AppendLine("stab torso share " + tf);
        if (!s.Apply(ADWoundRules.Arm, 9f, false) || s.severity[0] != 1) f.AppendLine("arm 9 dmg should be severity 1");
        if (s.Apply(ADWoundRules.Arm, 1f, false)) f.AppendLine("arm 10 should not raise");
        s.Apply(ADWoundRules.Arm, 9f, true);   // +18 counted -> 28 total -> severity 2 (thr 8,18,32)
        if (s.severity[0] != 2 || Math.Abs(s.HeavyDamageMul() - 0.75f) > 1e-4) f.AppendLine("arm sev " + s.severity[0] + " mul " + s.HeavyDamageMul());
        s.Apply(ADWoundRules.Leg, 40f, false);
        if (s.severity[1] != 3 || Math.Abs(s.SpeedTierPenalty() - 1.5f) > 1e-4) f.AppendLine("leg crippled should cost 2 tiers");
        int fired = 0; Action<ADWoundEvent> h = e => fired++; ADWoundSet.OnWound += h;
        s.Apply(ADWoundRules.Torso, 5f, false); ADWoundSet.OnWound -= h;
        if (fired != 1) f.AppendLine("OnWound not raised");
        s.Reset(); if (s.severity[0] != 0 || s.damage[1] != 0f) f.AppendLine("reset");
        float dm; s.RollAndApply("heavy", 10f, false, () => 0.999f, out dm);   // head x1.4
        if (Math.Abs(dm - 10f) > 1e-4 || s.severity[3] != 1) f.AppendLine("head multiplier " + dm);

        // cut plan
        var cc = new ADCutsCfg();
        cc.finishers = new[] {
            new ADCutFinisher { id = "decapitation", unit = "head" },
            new ADCutFinisher { id = "splitCrown", unit = "head_split" },
            new ADCutFinisher { id = "dismember", unit = "arm_right", also = new[] { "arm_left", "leg_right" }, alsoAtS = 0.35f },
            new ADCutFinisher { id = "opened", unit = "belly_open", fallback = "torso_waist" } };
        cc.bindings = new[] { new ADCutBinding { weapons = new[] { "sword" }, pool = new[] { new ADCutPick { id = "decapitation", weight = 3 }, new ADCutPick { id = "splitCrown", weight = 2 }, new ADCutPick { id = "opened", weight = 1 } } } };
        Func<string, bool> has = u => u != "belly_open";
        var a = new ADCutPicker(); var b = new ADCutPicker();
        string last = null; int none = 0, repeats = 0; var seen = new HashSet<string>();
        for (int i = 0; i < 60; i++)
        {
            string x = a.Pick(cc, "sword", 7), y = b.Pick(cc, "sword", 7);
            if (x != y) { f.AppendLine("pick not deterministic at " + i); break; }
            if (x == null) none++; else { seen.Add(x); if (x == last) repeats++; last = x; }
        }
        if (repeats > 0) f.AppendLine("no-repeat violated " + repeats);
        if (seen.Count != 3) f.AppendLine("expected 3 distinct finishers, got " + seen.Count);
        if (none > 12) f.AppendLine("too many plain deaths " + none);
        if (new ADCutPicker().Pick(cc, "spoon", 1) != null) f.AppendLine("unbound weapon should give plain death");
        var st = ADCutPicker.Steps(cc.finishers[3], has);
        if (st.Count != 1 || st[0].unit != "torso_waist") f.AppendLine("fallback unit not used");
        st = ADCutPicker.Steps(cc.finishers[2], u => u != "leg_right");
        if (st.Count != 2 || Math.Abs(st[1].atS - 0.35f) > 1e-5) f.AppendLine("also steps wrong");
        return f.ToString();
    }
}
