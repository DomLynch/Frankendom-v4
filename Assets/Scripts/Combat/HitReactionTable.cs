using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// P2 hit reactions by LOCATION x DIRECTION x WEIGHT, all from Assets/Data/hit_reactions.json (no numbers here).
// Duel.Resolve asks Pick() for the clean-hit flinch: it returns the Animator state to play (a state that really exists in the victim's controller:
// `prefer` is used when present, else `state`, else `fallback`, else defaultState), a stagger-tick scale (1 = sim unchanged) and the derived location/direction/weight,
// which also ride on CombatEvent and in HitReactionTable.Last for blood / cue consumers.
// Location: contact height if the caller knows it (contactY >= 0, metres above the victim's feet, divided by victim scale), else the move row (arms/legs by move), else its height.
// Direction: the attacker's position in the victim's frame (front / left / right / back). Face to face the swing side decides left/right (an attacker's right-hand swing lands on the victim's LEFT).
// Weight: kick for the kick move, heavy for charging / heavy-damage moves (same test as Duel.Resolve), light otherwise; a counter hit escalates light -> heavy (data).
// Lookup order: Resources/hit_reactions, StreamingAssets/hit_reactions.json, Application.dataPath/Data/hit_reactions.json. Missing file = legacy three-state behaviour (Pick returns null).
public static class HitReactionTable
{
    [Serializable] public class Heights { public float head = 1.5f, torso = 0.9f; }
    [Serializable] public class DirDeg { public float front = 55f, back = 90f; public bool mirrorSwing = true; }
    [Serializable] public class WeightRules { public string[] kickMoves = new string[] { "kick" }; public float heavyMinDamage = 18f; public bool heavyIfCharges = true, escalateOnCounter = true, escalateOnRear = false; }
    // moves[]: id (exact, or "*"), swingDirection (MoveDef.direction: overhead|thrust|left|right|low, or ""), location (head|torso|arm|legs), height (m, used when location empty), side (attacker-frame swing side: left|right|"")
    [Serializable] public class MoveRow { public string id = "", swingDirection = "", location = "", side = ""; public float height = 0f; }
    [Serializable] public class Row { public string character = "*", location = "*", direction = "*", weight = "*", state = "", prefer = "", fallback = "", note = ""; public float staggerScale = 1f; public bool knockdown = false; public float startS = 0f; /* PITMAULFIX v1: seconds into the hit clip where the reaction starts (0 = from the first frame) */ }
    [Serializable] public class Data
    {
        public int version = 1; public bool enabled = true, allowKnockdown = false, logPicks = false;
        public Heights heights = new Heights(); public DirDeg directions = new DirDeg(); public WeightRules weights = new WeightRules();
        public string defaultState = "hit_light_front";
        public MoveRow[] moves = new MoveRow[0]; public Row[] rows = new Row[0];
    }
    public class Result { public string location, direction, weight, state, character, moveId; public DuelFighter attacker; public float staggerScale = 1f; public bool knockdown; public Row row; public float startS; /* PITMAULFIX v1 */ }

    public static Result Last;   // the clean hit being resolved right now (set by Duel.Resolve before Fire, null for block / parry / guard break)
    static Data data;
    static readonly Dictionary<string, bool> stateCache = new Dictionary<string, bool>();
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; stateCache.Clear(); Load(); }

    public static string CharOf(DuelFighter f)
    {
        if (f == null) return "";
        return Roster.IdOf(f.transform);   // own name, then root name
    }

    static bool Match(string rowVal, string val) { return string.IsNullOrEmpty(rowVal) || rowVal == "*" || rowVal == val; }
    static int Score(string rowVal, string val) { return (string.IsNullOrEmpty(rowVal) || rowVal == "*") ? 0 : 1; }

    static MoveRow MoveRowFor(MoveDef m)
    {
        var d = Get(); if (m == null) return null; MoveRow byDir = null, any = null;
        for (int i = 0; i < d.moves.Length; i++)
        {
            var r = d.moves[i]; if (r == null) continue;
            if (!string.IsNullOrEmpty(r.id) && r.id != "*") { if (r.id == m.id) return r; continue; }
            if (!string.IsNullOrEmpty(r.swingDirection)) { if (byDir == null && r.swingDirection == m.direction) byDir = r; }
            else if (any == null) any = r;
        }
        return byDir ?? any;
    }

    public static string LocationOf(MoveDef m, float contactY, float victimScale)
    {
        var d = Get();
        if (contactY >= 0f) { float h = contactY / Mathf.Max(0.01f, victimScale); return h >= d.heights.head ? "head" : h >= d.heights.torso ? "torso" : "legs"; }
        var r = MoveRowFor(m);
        if (r != null)
        {
            if (!string.IsNullOrEmpty(r.location)) return r.location;
            if (r.height > 0f) return r.height >= d.heights.head ? "head" : r.height >= d.heights.torso ? "torso" : "legs";
        }
        return "torso";
    }

    public static string DirectionOf(DuelFighter attacker, DuelFighter victim, MoveDef m)
    {
        var d = Get(); if (attacker == null || victim == null) return "front";
        Vector3 to = attacker.transform.position - victim.transform.position; to.y = 0f;
        if (to.sqrMagnitude < 1e-4f) return "front";
        float ang = Vector3.SignedAngle(victim.transform.forward, to, Vector3.up);   // + = attacker on the victim's right
        float a = Mathf.Abs(ang);
        if (a >= d.directions.back) return "back";
        if (a > d.directions.front) return ang > 0f ? "right" : "left";
        var r = MoveRowFor(m);   // face to face: the swing side decides
        if (r != null && (r.side == "left" || r.side == "right"))
        {
            if (!d.directions.mirrorSwing) return r.side;
            return r.side == "left" ? "right" : "left";
        }
        return "front";
    }

    public static string WeightOf(MoveDef m, bool heavyFlag, bool counter, bool rear)
    {
        var d = Get(); var w = d.weights;
        if (m != null) for (int i = 0; i < w.kickMoves.Length; i++) if (w.kickMoves[i] == m.id) return "kick";
        bool heavy = heavyFlag || (m != null && ((w.heavyIfCharges && m.charges) || m.damage >= w.heavyMinDamage));
        if (!heavy && ((counter && w.escalateOnCounter) || (rear && w.escalateOnRear))) heavy = true;
        return heavy ? "heavy" : "light";
    }

    static bool HasState(DuelFighter f, string n)
    {
        if (f == null || string.IsNullOrEmpty(n)) return false;
        var anim = f.GetComponentInChildren<Animator>(); if (anim == null || anim.runtimeAnimatorController == null) return false;
        string key = anim.runtimeAnimatorController.name + "/" + n;
        if (stateCache.TryGetValue(key, out bool ok)) return ok;
        ok = anim.HasState(0, Animator.StringToHash(n)); stateCache[key] = ok; return ok;
    }

    // contactY < 0: unknown (Duel has no weapon contact point yet): the move row decides the location.
    public static Result Pick(DuelFighter attacker, DuelFighter victim, MoveDef m, bool heavyFlag, bool counter, bool rear, float contactY)
    {
        var d = Get(); if (!d.enabled || d.rows.Length == 0 || victim == null) return null;
        var res = new Result { character = CharOf(victim), attacker = attacker, moveId = m != null ? m.id : "" };
        res.location = LocationOf(m, contactY, victim.scale); res.direction = DirectionOf(attacker, victim, m); res.weight = WeightOf(m, heavyFlag, counter, rear);
        Row best = null; int bestScore = -1;
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i]; if (r == null) continue;
            if (!Match(r.character, res.character) || !Match(r.location, res.location) || !Match(r.direction, res.direction) || !Match(r.weight, res.weight)) continue;
            int s = Score(r.character, res.character) * 8 + Score(r.location, res.location) * 4 + Score(r.direction, res.direction) * 2 + Score(r.weight, res.weight);
            if (s >= bestScore) { bestScore = s; best = r; }
        }
        string state = d.defaultState;
        if (best != null)
        {
            res.row = best; res.staggerScale = best.staggerScale > 0f ? best.staggerScale : 1f;
            res.knockdown = best.knockdown && d.allowKnockdown;
            if (best.knockdown && !d.allowKnockdown) state = FirstThatExists(victim, best.fallback, best.state, d.defaultState);
            else state = FirstThatExists(victim, best.prefer, best.state, best.fallback, d.defaultState);
        }
        res.state = state; if (best != null && state == best.state && best.startS > 0f) res.startS = best.startS;   // PITMAULFIX v1: the row's own clip only (a `prefer` clip keeps its first frame)
        if (d.logPicks) Debug.Log($"[HitReaction] {res.character} {res.location}/{res.direction}/{res.weight} <- {(attacker ? attacker.name : "?")} {(m != null ? m.id : "?")} -> {res.state} x{res.staggerScale:F2}{(res.knockdown ? " KNOCKDOWN" : "")}");
        return res;
    }

    static string FirstThatExists(DuelFighter v, params string[] names)
    {
        for (int i = 0; i < names.Length; i++) if (!string.IsNullOrEmpty(names[i]) && HasState(v, names[i])) return names[i];
        return names[names.Length - 1];
    }

    static void Load()
    {
        data = new Data(); data.enabled = false;
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("hit_reactions");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "hit_reactions.json"); if (File.Exists(p)) json = DataPath.ReadFile(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/hit_reactions.json"); if (File.Exists(p)) json = DataPath.ReadFile(p); }
        }
        catch (Exception ex) { Debug.LogWarning("HitReactionTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("HitReactionTable: hit_reactions.json not found; legacy hit_light_front / hit_light_back / hit_heavy."); return; }
        try
        {
            var dd = JsonUtility.FromJson<Data>(json); if (dd == null) return;
            if (dd.heights == null) dd.heights = new Heights(); if (dd.directions == null) dd.directions = new DirDeg(); if (dd.weights == null) dd.weights = new WeightRules();
            if (dd.weights.kickMoves == null) dd.weights.kickMoves = new string[0]; if (dd.moves == null) dd.moves = new MoveRow[0]; if (dd.rows == null) dd.rows = new Row[0];
            data = dd;
        }
        catch (Exception ex) { Debug.LogError("HitReactionTable: parse failed: " + ex.Message); }
    }
}
