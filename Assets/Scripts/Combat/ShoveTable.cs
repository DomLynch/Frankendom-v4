using System;
using UnityEngine;

// SHOVE v1 (lanePITSHOVE): a free-hand SHOVE for a monster whose other hand holds a weapon (Pitborn: maul in the right hand, left hand empty). Data: Assets/Data/shove.json (+ the move row in move-defs.json,
// the clip row in attack_clips.json, the reaction row in hit_reactions.json, the cue rows in cues.json). MASTER `enabled` false (shipped) = today's fight bit for bit: nothing here touches the sim: no row is used, no RNG is drawn, no branch is taken (the shove.json file is only read once, to learn that it is off).
//  When on, and only for the characters that have a row (Pitborn), AIBrain asks Pick() once per decision tick: crowded (foe inside crowdM for crowdTicks), foe inside triggerMinM..triggerMaxM, cooldown over, stamina,
//  nothing noticed to answer, foe phase allowed, then ONE roll of this class's own seeded stream (never AiState.Roll: no existing AI draw moves). A pick is pressed like any move (DuelFighter.Press -> pMove -> StartMove):
//  the sim then runs the ordinary hit pipeline (Duel.Resolve: i-frames, guard side, shield cover, stamina, posture, poise, hit reaction), with two extra steps for a shove id only: ApplyKnock (the knock velocity that
//  puts the foe at pushToM) and NoKill (this move never kills).
// Command line: -shove:off (force off), -shove:on (force the master on), -shove:log (one line per shove, hit and push).
public static class ShoveTable
{
    [Serializable] public class MovePick { public string id = ""; public float weight = 1f; }
    [Serializable]
    public class Row
    {
        public string character = ""; public bool enabled;
        public float triggerMinM, triggerMaxM = 1.95f, crowdM = 2.05f;          // centre to centre, metres (DodgeTable.minSeparation is 1.5)
        public int crowdTicks = 30, cooldownTicks = 600, initialCooldownTicks = 300, retryTicks = 45;
        public float chance = 0.55f, minStamina = 30f;
        public bool requireNoThreat = true;
        public string[] foePhases = new string[0];                                // Ph names: Ready, Guard, Attack, Hurt, Roll, Backstep
        public MovePick[] moves = new MovePick[0];
    }
    [Serializable] public class MoveRow { public string id = ""; public float pushToM = 2.6f, minPushM = 0.5f, maxPushM = 1.4f; public bool nonLethal = true; public string location = ""; }
    [Serializable] public class Data { public bool enabled; public bool debugLog; public float knockDecay = 0.88f; public int minSlideTicks = 8; /* DATALAYER b9 */ public Row[] rows = new Row[0]; public MoveRow[] moves = new MoveRow[0]; }

    // per-brain state (created by AIBrain the first time a row applies)
    public class AiState { public int crowd, cooldownUntil, nextRoll; public uint rng; public bool init; public Row row; public bool rowResolved; }

    static Data data; static int force;       // force: 1 = -shove:on, -1 = -shove:off
    public static int Started, Landed, Blocked, Whiffed;   // QA counters (-shove:log prints them with each event)
    static bool Flag(string f) { foreach (var a in Environment.GetCommandLineArgs()) if (a == f) return true; return false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; force = 0; Started = Landed = Blocked = Whiffed = 0; ContactRelay.OnAttackStarted -= OnStarted; ContactRelay.OnAttackMissed -= OnMissed; ContactRelay.OnContact -= OnContact; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { ContactRelay.OnAttackStarted -= OnStarted; ContactRelay.OnAttackStarted += OnStarted; ContactRelay.OnAttackMissed -= OnMissed; ContactRelay.OnAttackMissed += OnMissed; ContactRelay.OnContact -= OnContact; ContactRelay.OnContact += OnContact; }

    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }
    // the master: the file's `enabled`, overridden by -shove:on / -shove:off
    public static bool Active { get { if (data == null) Load(); return force > 0 || (force == 0 && data.enabled); } }
    static bool Log { get { return Get().debugLog || Flag("-shove:log"); } }

    static void Load()
    {
        data = new Data(); force = Flag("-shove:off") ? -1 : Flag("-shove:on") ? 1 : 0;
        string json = DataPath.ReadText("shove.json");
        if (string.IsNullOrEmpty(json)) return;                                   // no file = today
        try
        {
            var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿'));
            if (d == null) return;
            if (d.rows == null) d.rows = new Row[0]; if (d.moves == null) d.moves = new MoveRow[0];
            data = d;
        }
        catch (Exception ex) { Debug.LogWarning("[shove] shove.json parse failed, shove stays off: " + ex.Message); data = new Data(); }
    }

    public static MoveRow MoveRowOf(string id)
    {
        var m = Get().moves; if (m == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < m.Length; i++) if (m[i] != null && m[i].id == id) return m[i];
        return null;
    }
    // true for a move id that has a shove.json moves[] row, while the master is on (everything else: false, so every hook below is a no-op when off)
    public static bool IsMove(string id) { return Active && MoveRowOf(id) != null; }
    public static bool NoKill(MoveDef m) { if (!Active || m == null) return false; var r = MoveRowOf(m.id); return r != null && r.nonLethal; }

    // the row that applies to this fighter (null = none: off, or not a character with a row)
    public static Row RowFor(DuelFighter f)
    {
        if (!Active || f == null) return null;
        var rows = Get().rows;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r != null && r.enabled && RosterResolver.NameIs(r.character, f.name)) return r; }
        return null;
    }

    static uint Hash(string s) { uint h = 2166136261u; for (int i = 0; i < s.Length; i++) h = unchecked((h ^ s[i]) * 16777619u); return h; }   // hc:ok DATALAYER b9: FNV-1a offset basis and prime, hash algorithm constants
    static float Next(AiState st) { uint x = st.rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; st.rng = x; return (x & 0xFFFFFFu) / 16777216f; }   // hc:ok DATALAYER b9: xorshift32 shifts and the 24-bit mantissa mask, RNG algorithm constants

    // AIBrain.Decide, once per decision: keep the crowding count (consecutive sim ticks the foe was inside crowdM)
    public static void Track(AiState st, Row r, DuelFighter df, float dist, int tick, int n, int seedOffset)
    {
        if (!st.init)
        {
            st.init = true; st.row = r;
            st.rng = unchecked((uint)AiRows.I.dial.startSeed ^ Hash(df.name) ^ (uint)AIBrain.GlobalSeed ^ (uint)seedOffset ^ 0x53484F56u); if (st.rng == 0u) st.rng = 2463534242u;   // hc:ok DATALAYER b9: xorshift32 must not start at 0, standard non-zero seed
            st.cooldownUntil = tick + Mathf.Max(0, r.initialCooldownTicks); st.nextRoll = 0; st.crowd = 0;
        }
        if (dist <= r.crowdM) st.crowd += Mathf.Max(1, n); else st.crowd = 0;
    }

    static bool PhaseOk(Row r, Ph p)
    {
        if (r.foePhases == null || r.foePhases.Length == 0) return true;
        string s = p.ToString();
        for (int i = 0; i < r.foePhases.Length; i++) if (r.foePhases[i] == s) return true;
        return false;
    }

    // AIBrain.Decide offence hook: the move id to press now, or null
    public static string Pick(AiState st, Row r, DuelFighter df, DuelFighter foe, float dist, Ph foePhase, int tick)
    {
        if (st == null || !st.init || r.moves == null || r.moves.Length == 0) return null;
        if (st.crowd < r.crowdTicks) return null;
        if (dist < r.triggerMinM || dist > r.triggerMaxM) return null;
        if (tick < st.cooldownUntil || tick < st.nextRoll) return null;
        if (df.stamina < r.minStamina) return null;
        if (!PhaseOk(r, foePhase)) return null;
        float total = 0f; for (int i = 0; i < r.moves.Length; i++) if (r.moves[i] != null && df.moves != null && df.moves.Get(r.moves[i].id) != null) total += Mathf.Max(0f, r.moves[i].weight);
        if (total <= 0f) return null;                                              // the move row is not in move-defs.json: nothing to press
        if (Next(st) >= r.chance) { st.nextRoll = tick + Mathf.Max(1, r.retryTicks); return null; }
        float pick = Next(st) * total; string id = null;
        for (int i = 0; i < r.moves.Length; i++)
        {
            if (r.moves[i] == null || df.moves == null || df.moves.Get(r.moves[i].id) == null) continue;
            pick -= Mathf.Max(0f, r.moves[i].weight); id = r.moves[i].id; if (pick <= 0f) break;
        }
        st.cooldownUntil = tick + Mathf.Max(1, r.cooldownTicks); st.crowd = 0;
        if (Log) Debug.Log("[shove] " + df.name + " picks " + id + " at tick " + tick + " foe " + foe.name + " dist " + dist.ToString("0.00") + " m stamina " + df.stamina.ToString("0"));
        return id;
    }

    // Duel.Resolve, clean hit, after SetKnock: the knock velocity that carries the foe out to pushToM over its stagger (MoveTick: v = knock; knock *= decay, one step per stagger tick)
    public static void ApplyKnock(DuelFighter a, DuelFighter d, MoveDef m, float stagger, HitReactionTable.Result hr, float knockMul)
    {
        if (!Active || m == null || knockMul <= 0f) return;                       // absorbed / grace (poise): the foe plants its feet, as for every move
        var mv = MoveRowOf(m.id); if (mv == null) return;
        Vector3 ad = d.transform.position - a.transform.position; ad.y = 0f; float dist = ad.magnitude; if (dist < 0.01f) return;   // hc:ok DATALAYER b9: degenerate-direction guard (foe exactly on the shover)
        int n = Mathf.Max(Get().minSlideTicks, (int)(stagger /* DATALAYER b9 */ * (hr != null ? hr.staggerScale : 1f)));
        float k = Mathf.Clamp(Get().knockDecay, 0.05f, 0.995f);   // hc:ok DATALAYER b9: sanity bounds on a data value (decay 0 or 1 would break the slide maths)
        float need = Mathf.Clamp(mv.pushToM - dist, mv.minPushM, mv.maxPushM);
        float v0 = need * 60f * (1f - k) / (1f - Mathf.Pow(k, n));
        d.SetKnock(ad / dist * v0 * knockMul);
        if (Log) Debug.Log("[shove] hit " + d.name + " from " + dist.ToString("0.00") + " m: push " + need.ToString("0.00") + " m (v0 " + v0.ToString("0.0") + " m/s over " + n + " stagger ticks) -> " + (dist + need).ToString("0.00") + " m");
    }

    static void OnStarted(DuelFighter f, MoveDef m) { if (m != null && IsMove(m.id)) { Started++; } }
    static void OnMissed(DuelFighter f, MoveDef m) { if (m != null && IsMove(m.id)) { Whiffed++; if (Log) Debug.Log("[shove] " + f.name + " whiffed " + m.id + " (counters started " + Started + " landed " + Landed + " blocked " + Blocked + " whiffed " + Whiffed + ")"); } }
    static void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (a == null || a.CurrentMove == null || !IsMove(a.CurrentMove.id)) return;
        if (type == "Hit") Landed++; else if (type == "Blocked" || type == "ShieldBlocked") Blocked++;
        if (Log) Debug.Log("[shove] " + a.name + " " + a.CurrentMove.id + " -> " + type + " on " + (d != null ? d.name : "?") + " (started " + Started + " landed " + Landed + " blocked " + Blocked + " whiffed " + Whiffed + ")");
    }
}
