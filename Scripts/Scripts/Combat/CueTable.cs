using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// P2 cue table (Assets/Data/cues.json): which CueBus sprite cue(s) play for an event, by weapon (attacker), surface (defender), weight and move.
// CueBus keeps its V3 clips and its voice/frame limits; this only decides WHAT to play. Dispatch() returns false when the table is off or has no row for the event,
// so CueBus falls back to its old hard-coded mapping (a missing cues.json changes nothing).
// Most specific row wins per layer (impact / voice / base): moves 16, weapon 8, surface 4, attacker 2, defender 2, weight 1; a later row wins a tie. Disabled rows never play.
public static class CueTable
{
    [Serializable] public class PlayRow { public string cue = ""; public float gain = 1f, delay = 0f, rate = 1f, chance = 1f; public string who = ""; }
    [Serializable] public class Row { public string evt = "", layer = "impact", weapon = "*", surface = "*", weight = "*", attacker = "*", defender = "*", note = ""; public string[] moves = new string[0]; public bool disabled = false; public PlayRow[] play = new PlayRow[0]; }
    [Serializable] public class CharWeapon { public string character = "", weapon = ""; }
    [Serializable] public class CharSurface { public string character = "", surface = ""; }
    [Serializable] public class CharRate { public string character = ""; public float rate = 1f; }
    [Serializable] public class Data { public int version = 1; public bool enabled = true; public CharWeapon[] weapons = new CharWeapon[0]; public CharSurface[] surfaces = new CharSurface[0]; public CharRate[] voicePitch = new CharRate[0]; public Row[] rows = new Row[0]; }

    static Data data; static uint seed = 977;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }
    static float Rnd() { unchecked { seed += 0x6d2b79f5u; uint t = (seed ^ (seed >> 15)) * (1u | seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }

    static bool Any(string v) { return string.IsNullOrEmpty(v) || v == "*"; }
    public static string WeaponOf(string character) { string held = WeaponSwap.CueWeaponFor(character); if (held.Length > 0) return held; /* WEAPONSWAP v88 */ var d = Get(); if (string.IsNullOrEmpty(character)) return ""; for (int i = 0; i < d.weapons.Length; i++) if (d.weapons[i] != null && RosterResolver.KeyIs(d.weapons[i].character, character) && d.weapons[i].character != "*") return d.weapons[i].weapon; if (RosterResolver.InRoster(character)) { string rw = Roster.Get(character).weapon; if (rw.Length > 0) return rw; } for (int i = 0; i < d.weapons.Length; i++) if (d.weapons[i] != null && d.weapons[i].character == "*") return d.weapons[i].weapon; return ""; }   // HCKIT2 v93 exact row, then roster.weapon, then "*"
    public static string SurfaceOf(string character) { var d = Get(); if (string.IsNullOrEmpty(character)) return ""; for (int i = 0; i < d.surfaces.Length; i++) if (d.surfaces[i] != null && RosterResolver.KeyIs(d.surfaces[i].character, character) && d.surfaces[i].character != "*") return d.surfaces[i].surface; for (int i = 0; i < d.surfaces.Length; i++) if (d.surfaces[i] != null && d.surfaces[i].character == "*") return d.surfaces[i].surface; return ""; }   // HCKIT2 v93 exact row, then "*"
    public static float VoiceRate(string character) { var d = Get(); if (string.IsNullOrEmpty(character)) return 1f; for (int i = 0; i < d.voicePitch.Length; i++) if (d.voicePitch[i] != null && RosterResolver.KeyIs(d.voicePitch[i].character, character) && d.voicePitch[i].character != "*") return d.voicePitch[i].rate; for (int i = 0; i < d.voicePitch.Length; i++) if (d.voicePitch[i] != null && d.voicePitch[i].character == "*") return d.voicePitch[i].rate; return 1f; }   // HCKIT2 v93 exact row, then "*"

    // weight: "light" | "heavy" | "kick" (use HitReactionTable.Last.weight for a landed hit, else the swing's class). Returns true when at least one row matched.
    public static bool Dispatch(CueBus bus, string evt, DuelFighter attacker, DuelFighter defender, string weight, string moveId)
    {
        var d = Get(); if (!d.enabled || bus == null) return false;
        string ac = HitReactionTable.CharOf(attacker), dc = HitReactionTable.CharOf(defender);
        string weapon = WeaponOf(ac), surface = SurfaceOf(dc);
        var best = new Dictionary<string, Row>(); var score = new Dictionary<string, int>();
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i]; if (r == null || r.evt != evt) continue;
            if (!Any(r.weapon) && r.weapon != weapon) continue; if (!Any(r.surface) && r.surface != surface) continue; if (!Any(r.weight) && r.weight != weight) continue;
            if (!Any(r.attacker) && r.attacker != ac) continue; if (!Any(r.defender) && r.defender != dc) continue;
            int mv = 0; if (r.moves != null && r.moves.Length > 0) { if (Array.IndexOf(r.moves, moveId ?? "") < 0) continue; mv = 16; }
            string layer = string.IsNullOrEmpty(r.layer) ? "impact" : r.layer;
            int s = mv + (Any(r.weapon) ? 0 : 8) + (Any(r.surface) ? 0 : 4) + (Any(r.attacker) ? 0 : 2) + (Any(r.defender) ? 0 : 2) + (Any(r.weight) ? 0 : 1);
            if (r.disabled) { s = -1; }   // a disabled row still shadows nothing: skip it entirely
            if (s < 0) continue;
            if (!score.TryGetValue(layer, out int cur) || s >= cur) { score[layer] = s; best[layer] = r; }
        }
        if (best.Count == 0) return false;
        foreach (var kv in best)
        {
            var r = kv.Value;
            for (int p = 0; p < r.play.Length; p++)
            {
                var pr = r.play[p]; if (pr == null || string.IsNullOrEmpty(pr.cue)) continue;
                if (pr.chance < 1f && Rnd() > pr.chance) continue;
                float rate = pr.rate <= 0f ? 1f : pr.rate;
                if (pr.who == "attacker") rate *= VoiceRate(ac); else if (pr.who == "defender") rate *= VoiceRate(dc);
                bus.Play(pr.cue, pr.gain, pr.delay, rate);
            }
        }
        return true;
    }

    static void Load()
    {
        data = new Data(); data.enabled = false; string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("cues");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "cues.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/cues.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("CueTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("CueTable: cues.json not found; CueBus uses its built-in mapping."); return; }
        try
        {
            var dd = JsonUtility.FromJson<Data>(json); if (dd == null) return;
            if (dd.weapons == null) dd.weapons = new CharWeapon[0]; if (dd.surfaces == null) dd.surfaces = new CharSurface[0]; if (dd.voicePitch == null) dd.voicePitch = new CharRate[0]; if (dd.rows == null) dd.rows = new Row[0];
            for (int i = 0; i < dd.rows.Length; i++) if (dd.rows[i] != null) { if (dd.rows[i].play == null) dd.rows[i].play = new PlayRow[0]; if (dd.rows[i].moves == null) dd.rows[i].moves = new string[0]; }
            data = dd;
        }
        catch (Exception ex) { Debug.LogError("CueTable: parse failed: " + ex.Message); }
    }
}
