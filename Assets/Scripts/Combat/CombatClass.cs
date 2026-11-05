using System;
using UnityEngine;

// HCKIT4 v95 (HC3 K8): the ONE place for the combat event / move vocabulary. CamShake, CameraImpact, HitFeel, PresentationClock, CombatSlowMo and CueBus each re-derived
// "is this move a kick / a special / a crit / heavy" from their own string literals (7 copies). They now ask this class; the move-id vocabulary lives in combat_classes.json
// (a new kick / special / crit move id is ONE data edit). Event types and class names are the keys other json files use (hit_feel tiers, camera_shake rows ...), so they stay constants.
public static class CombatClass
{
    // CombatEvent.type values
    public const string Hit = "Hit", Blocked = "Blocked", Parried = "Parried", GuardBroken = "GuardBroken";
    // class / tier names (the row ids of camera_shake.json, hit_feel.json, hitstop tiers, slow_mo.json)
    public const string HitHeavy = "hitHeavy", HitLight = "hitLight", BlockHeavy = "blockHeavy", BlockLight = "blockLight", Parry = "parry", GuardBreak = "guardBreak",
        Kick = "kick", Special = "special", Crit = "crit", Deathblow = "deathblow", Kill = "kill";

    [Serializable]
    public class Data
    {
        public string kickMoveId = "kick", specialMovePrefix = "skill_", critMoveId = "critical";
        public string[] heavyMovePrefixes = { "heavy", "skill_" };      // a move whose id starts with one of these swings as a heavy (cue weight, whoosh)
        public string[] heavyMoveIds = { "riposte", "slash_riposte" };  // ... or equals one of these
        public string[] stabMoveContains = { "thrust" };                // the killing blow is a stab when the move id contains one of these ...
        public string[] stabMoveIds = { "riposte" };                    // ... or equals one of these
    }

    static Data data;
    public static Data Get()
    {
        if (data != null) return data;
        var d = new Data();
        try { string s = DataPath.ReadText("combat_classes.json"); if (!string.IsNullOrEmpty(s)) { var r = JsonUtility.FromJson<Data>(s); if (r != null) d = r; } }
        catch (Exception ex) { Debug.LogWarning("CombatClass: combat_classes.json read failed: " + ex.Message); }
        if (d.heavyMovePrefixes == null) d.heavyMovePrefixes = new string[0]; if (d.heavyMoveIds == null) d.heavyMoveIds = new string[0];
        if (d.stabMoveContains == null) d.stabMoveContains = new string[0]; if (d.stabMoveIds == null) d.stabMoveIds = new string[0];
        data = d; return d;
    }
    public static void Reload() { data = null; }

    public static string KickMoveId { get { return Get().kickMoveId; } }
    public static string SpecialPrefix { get { return Get().specialMovePrefix; } }

    public static bool IsKick(string moveId) { return !string.IsNullOrEmpty(moveId) && moveId == Get().kickMoveId; }
    public static bool IsSpecial(string moveId) { var p = Get().specialMovePrefix; return !string.IsNullOrEmpty(moveId) && !string.IsNullOrEmpty(p) && moveId.StartsWith(p, StringComparison.Ordinal); }
    /// <summary>counter, rear hit or the critical move (the old "e.counter || e.rear || moveId == critical")</summary>
    public static bool IsCrit(CombatEvent e) { return e.counter || e.rear || (!string.IsNullOrEmpty(e.moveId) && e.moveId == Get().critMoveId); }
    public static bool IsHeavyMove(MoveDef m)
    {
        if (m == null || m.id == null) return false; var d = Get(); string id = m.id;
        for (int i = 0; i < d.heavyMovePrefixes.Length; i++) if (!string.IsNullOrEmpty(d.heavyMovePrefixes[i]) && id.StartsWith(d.heavyMovePrefixes[i], StringComparison.Ordinal)) return true;
        for (int i = 0; i < d.heavyMoveIds.Length; i++) if (id == d.heavyMoveIds[i]) return true;
        return false;
    }
    public static bool IsStab(MoveDef m)
    {
        if (m == null || m.id == null) return false; var d = Get(); string id = m.id;
        for (int i = 0; i < d.stabMoveContains.Length; i++) if (!string.IsNullOrEmpty(d.stabMoveContains[i]) && id.Contains(d.stabMoveContains[i])) return true;
        for (int i = 0; i < d.stabMoveIds.Length; i++) if (id == d.stabMoveIds[i]) return true;
        return false;
    }
    /// <summary>The plain class of a contact by type and weight (no kick / special / kill / crit logic): hitHeavy | hitLight | blockHeavy | blockLight | parry | guardBreak | null.</summary>
    public static string Of(string type, bool heavy)
    {
        switch (type)
        {
            case Hit: return heavy ? HitHeavy : HitLight;
            case Blocked: return heavy ? BlockHeavy : BlockLight;
            case Parried: return Parry;
            case GuardBroken: return GuardBreak;
        }
        return null;
    }
}
