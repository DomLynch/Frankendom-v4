using System;
using System.Collections.Generic;
using UnityEngine;

// SURPRISE GATE v1 (pool lane S1 authors it; every surprise lane copies this IDENTICAL file to Assets\Scripts\Surprise\SurpriseGate.cs). Data: surprise_director.json { enabled, maxPerVisit, seed } (Assets\Data + StreamingAssets, read via DataPath).
// ONE surprise per zone visit by default: a surprise calls TryClaim(zone, key) right before it fires; the first claim of a visit wins, later ones return false until Reset(zone) (called when Joan enters the zone again).
// enabled false (or a missing file) = TryClaim is always false = no surprise fires. ForceEnable is for harness proof args only (call sites sit under #if !FRANKEN_NO_HARNESS).
// Seed: surprise_director.json seed, or 0 = a per-run value taken once when the game starts (so every run differs); Seed(salt) mixes in a string so each surprise gets its own stream.
public static class SurpriseGate
{
    [Serializable] class Cfg { public bool enabled = false; public int maxPerVisit = 1, seed = 0; }
    const string FileName = "surprise_director.json";
    static Cfg c; static bool forced; static readonly Dictionary<string, int> claims = new Dictionary<string, int>(); static readonly uint runId = (uint)(DateTime.UtcNow.Ticks ^ (DateTime.UtcNow.Ticks >> 32));

    static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try { string t = DataPath.ReadText(FileName); if (!string.IsNullOrEmpty(t)) { var k = JsonUtility.FromJson<Cfg>(t.TrimStart((char)0xFEFF)); if (k != null) c = k; } }
        catch (Exception e) { Debug.LogWarning("[SurpriseGate] read failed, off: " + e.Message); c = new Cfg(); }
        return c;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; forced = false; claims.Clear(); }

    public static bool Enabled { get { return forced || Get().enabled; } }
    public static void ForceEnable(bool on) { forced = on; }   // harness proof only

    public static bool TryClaim(string zone, string key)
    {
        if (!Enabled || string.IsNullOrEmpty(zone)) return false;
        int n; claims.TryGetValue(zone, out n);
        if (n >= Mathf.Max(1, Get().maxPerVisit)) { Debug.Log("[SurpriseGate] refused " + key + " in " + zone + " (already " + n + " this visit)"); return false; }   // hc:ok floor of one
        claims[zone] = n + 1; Debug.Log("[SurpriseGate] claimed " + key + " in " + zone + " (" + (n + 1) + "/" + Get().maxPerVisit + ")"); return true;
    }

    public static void Reset(string zone) { if (zone != null) claims.Remove(zone); }

    // stable for the whole run, different every run unless seed is set in the json; salt = a per-surprise name
    public static uint Seed(string salt)
    {
        uint h = 2166136261u; uint s = Get().seed != 0 ? (uint)Get().seed : runId;   // hc:ok FNV-1a constants
        h = (h ^ s) * 16777619u; if (salt != null) foreach (char ch in salt) h = (h ^ ch) * 16777619u;   // hc:ok FNV-1a constants
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; return h;   // hc:ok FNV-1a constants
    }
}
