using System;
using System.Collections.Generic;
using UnityEngine;

// V62B: ground contact of BOTH fighters while a finisher scene runs, plus the probe the -finisherauto harness judges.
//  * lowest sole (Foot / Toe_End bones minus a per-fighter sole offset, data rt.ground.soles) against the real ground under that foot (raycast, fighter layers + pieces skipped), every LateUpdate AFTER the Animator / FootGround.
//  * correction (rt.ground.correct): a body whose sole sinks below the ground is lifted (the dc_fall / dc_settle "Dracula sunk to the waist" frames); the killer, and the victim once dead, is also lowered when it floats.
//    The whole root moves (the paired Mixamo clips have no ground information).
//  * statistics: raw (before correction) and corrected gap, min / max, while `Judge` is set (killer: from the contact; victim: from Die() + victimJudgeAfterDieS; both: the final hold).
// The component removes itself when no scene is running (RTFinisher.Busy false), so it never touches normal gameplay. Everything tunable = finishers.json rt.ground.
[Serializable] public class SoleRow { public string fighter = "Dracula"; public float footM = 0.09f, toeEndM = 0.06f; }   // sole = bone height - offset (world metres)

[Serializable]
public class FinisherGroundCfg
{
    public bool enabled = true;           // attach the ground probe / correction to both fighters during a finisher scene
    public bool correct = true;           // false = measure only (the raw gap is then what fails)
    public float sunkTolM = 0.04f;        // lift the body when the lowest sole is further below the ground than this
    public float floatTolM = 0.03f;       // lower the killer (and the dead victim) when the lowest sole is higher above the ground than this
    public float maxMoveM = 0.8f;         // one correction never moves more than this
    public float rayUpM = 1.0f, rayDownM = 3.0f;
    public float failAboveM = 0.10f, failBelowM = 0.10f;   // -finisherauto: corrected gap above / below the ground beyond this FAILS (raw is reported, WARN)
    public float victimJudgeAfterDieS = 0.4f;               // the victim is judged from Die() + this (it is airborne / staggering before)
    public float killerJudgeAfterContactS = 0.0f;           // the killer is judged from the contact + this
    public int[] ignoreLayers = { 8, 9 };
    public SoleRow[] soles = { new SoleRow { fighter = "Dracula", footM = 0.09f, toeEndM = 0.06f }, new SoleRow { fighter = "Pitborn", footM = 0.17f, toeEndM = 0.06f } };
}

public struct GroundStat
{
    public string who; public int n; public float rawMax, rawMin, max, min, moved; public bool any;
    public string Line()
    {
        if (!any || n == 0) return who + ": no samples";
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return who + ": gap to ground max " + max.ToString("+0.000;-0.000", ic) + " min " + min.ToString("+0.000;-0.000", ic) + " m (raw max " + rawMax.ToString("+0.000;-0.000", ic) + " min " + rawMin.ToString("+0.000;-0.000", ic) + ", corrected " + moved.ToString("0.00", ic) + " m total, " + n + " samples)";
    }
}

[DefaultExecutionOrder(32000)]   // after the Animator, FootGround (-100) and Fighter (500)
public sealed class FinisherGround : MonoBehaviour
{
    public bool IsKiller, Judge, CanLower;
    public static GroundStat Killer, Victim;
    static readonly RaycastHit[] hits = new RaycastHit[24];

    DuelFighter df; CharacterController cc; Transform[] bones; float[] off; FinisherGroundCfg C; int mask; float born;

    public static FinisherGround Attach(DuelFighter f, bool killer)
    {
        if (f == null) return null;
        var C = FinisherConfig.Get().rt.ground; if (C == null || !C.enabled) return null;
        var g = f.GetComponent<FinisherGround>(); if (g == null) g = f.gameObject.AddComponent<FinisherGround>();
        g.IsKiller = killer; g.Judge = false; g.CanLower = killer; g.df = f; g.C = C; g.born = Time.unscaledTime;
        var st = new GroundStat { who = (killer ? "killer " : "victim ") + f.name, rawMax = -99f, rawMin = 99f, max = -99f, min = 99f };
        if (killer) Killer = st; else Victim = st;
        return g;
    }
    public static void ResetStats() { Killer = default(GroundStat); Victim = default(GroundStat); }

    void Init()
    {
        if (C == null) C = FinisherConfig.Get().rt.ground;
        if (df == null) df = GetComponent<DuelFighter>();
        cc = GetComponent<CharacterController>();
        mask = ~0; if (C.ignoreLayers != null) foreach (var l in C.ignoreLayers) mask &= ~(1 << l);
        float foot = 0.09f, toe = 0.06f; string nm = name;
        if (C.soles != null) foreach (var s in C.soles) if (s != null && !string.IsNullOrEmpty(s.fighter) && nm.IndexOf(s.fighter, StringComparison.OrdinalIgnoreCase) >= 0) { foot = s.footM; toe = s.toeEndM; }
        var list = new List<Transform>(); var offs = new List<float>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name;
            if (n.EndsWith("Toe_End", StringComparison.OrdinalIgnoreCase)) { list.Add(t); offs.Add(toe); }
            else if (n.EndsWith("LeftFoot", StringComparison.OrdinalIgnoreCase) || n.EndsWith("RightFoot", StringComparison.OrdinalIgnoreCase)) { list.Add(t); offs.Add(foot); }
        }
        bones = list.ToArray(); off = offs.ToArray();
    }

    // ground height under a point (nearest hit that is not this body, another fighter's piece or a layer in ignoreLayers)
    float GroundY(Vector3 p, out bool found)
    {
        found = false; float best = float.MaxValue, y = 0f;
        int n = Physics.RaycastNonAlloc(p + Vector3.up * C.rayUpM, Vector3.down, hits, C.rayUpM + C.rayDownM, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;
            if (h.collider.GetComponentInParent<FinisherPiece>() != null) continue;
            if (h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; found = true; }
        }
        return y;
    }

    void LateUpdate()
    {
        if (!RTFinisher.Busy && Time.unscaledTime - born > 0.5f) { Destroy(this); return; }
        if (C == null || bones == null) Init();
        if (bones == null || bones.Length == 0) return;
        float gap = float.MaxValue;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i]; if (b == null) continue;
            bool ok; float gy = GroundY(b.position, out ok); if (!ok) continue;
            float g = (b.position.y - off[i]) - gy; if (g < gap) gap = g;
        }
        if (gap == float.MaxValue) return;
        float raw = gap, dy = 0f;
        if (C.correct)
        {
            if (raw < -C.sunkTolM) dy = Mathf.Min(-raw, C.maxMoveM);
            else if (CanLower && raw > C.floatTolM) dy = -Mathf.Min(raw, C.maxMoveM);
            if (Mathf.Abs(dy) > 0.002f) { Move(dy); gap = raw + dy; } else dy = 0f;
        }
        if (Judge)
        {
            var s = IsKiller ? Killer : Victim; s.any = true; s.n++; s.moved += Mathf.Abs(dy);
            s.rawMax = Mathf.Max(s.rawMax, raw); s.rawMin = Mathf.Min(s.rawMin, raw); s.max = Mathf.Max(s.max, gap); s.min = Mathf.Min(s.min, gap);
            if (IsKiller) Killer = s; else Victim = s;
        }
    }

    void Move(float dy)
    {
        bool had = cc != null && cc.enabled; if (had) cc.enabled = false;
        var p = transform.position; p.y += dy; transform.position = p;
        if (had) cc.enabled = true;
    }
}
