using System;
using System.IO;
using UnityEngine;

// P2 blood scaling as DATA (Assets/Data/blood.json): how much blood a hit throws by hit weight (light / heavy / kick), hit location (head / torso / arm / legs) and who is hit
// (flesh vs armoured: roster.json characters[].armoured). Dom's rule: blood is dark, non-glowing droplets on flesh; sparks and flash only on metal / guard contact (victims[].sparks / flash).
// Consumers: BloodBurst (strand count, speed, lift), CombatVFX (drops, mist, sparks, flash), BloodGround (ground splats).
// All multipliers are 1 when blood.json is missing, so the V3 numbers stay exactly as they are today.
public static class BloodTable
{
    [Serializable] public class Mul { public string id = ""; public float count = 1f, drops = 1f, speed = 1f, up = 1f, mist = 1f, ground = 1f; }
    [Serializable] public class Victim { public string match = ""; public float bloodScale = 1f; public bool sparks = false, flash = false; }
    [Serializable] public class GroundCfg
    {
        public bool enabled = true; public int max = 24, texSize = 64, seed = 29; public float lifeSec = 45f, fadeSec = 8f, yOffset = 0.012f, rayUp = 0.6f, rayDown = 2.2f;
        public float minRadius = 0.2f, maxRadius = 0.85f, minSize = 0.1f, maxSize = 0.32f, alpha = 0.8f, killSizeMul = 1.5f, behindMul = 0.35f;
        public float[] color = new float[] { 0.11f, 0.008f, 0.008f }; public int light = 1, heavy = 2, kick = 0, kill = 4;
    }
    [Serializable] public class Data
    {
        public int version = 1; public bool enabled = true; public float killScale = 1f;
        public Mul[] weights = new Mul[0], locations = new Mul[0]; public Victim[] victims = new Victim[0]; public GroundCfg ground = new GroundCfg();
    }
    public struct Scale { public float count, drops, speed, up, mist, ground, victim; public bool sparks, flash; }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    static Mul Find(Mul[] a, string id) { if (a != null && id != null) for (int i = 0; i < a.Length; i++) if (a[i] != null && a[i].id == id) return a[i]; return null; }

    // legacy default when no victims[] row matches: the roster row's armoured flag (metal sparks / flashes, flesh bleeds)
    static bool Armoured(DuelFighter v) { return v != null && Roster.Get(Roster.IdOfName(v.name)).armoured; }

    // weight / location come from HitReactionTable.Last (null = legacy: ev.heavy picks heavy/light, torso)
    public static Scale For(DuelFighter victim, bool heavy, bool kill)
    {
        var d = Get(); var s = new Scale { count = 1f, drops = 1f, speed = 1f, up = 1f, mist = 1f, ground = 1f, victim = 1f, sparks = Armoured(victim), flash = Armoured(victim) };
        if (!d.enabled) return s;
        var h = HitReactionTable.Last;
        string w = h != null ? h.weight : (heavy ? "heavy" : "light"), loc = h != null ? h.location : "torso";
        var mw = Find(d.weights, w); var ml = Find(d.locations, loc);
        if (mw != null) { s.count *= mw.count; s.drops *= mw.drops; s.speed *= mw.speed; s.up *= mw.up; s.mist *= mw.mist; s.ground *= mw.ground; }
        if (ml != null) { s.count *= ml.count; s.drops *= ml.drops; s.speed *= ml.speed; s.up *= ml.up; s.mist *= ml.mist; s.ground *= ml.ground; }
        if (kill) { s.count *= d.killScale; s.drops *= d.killScale; s.mist *= d.killScale; }
        if (victim != null && d.victims != null)
            for (int i = 0; i < d.victims.Length; i++)
            {
                var v = d.victims[i]; if (v == null || string.IsNullOrEmpty(v.match) || !victim.name.Contains(v.match)) continue;
                s.victim = v.bloodScale; s.sparks = v.sparks; s.flash = v.flash; s.count *= v.bloodScale; s.drops *= v.bloodScale; s.mist *= v.bloodScale; s.ground *= v.bloodScale; break;
            }
        return s;
    }

    static void Load()
    {
        data = new Data(); string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("blood");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "blood.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/blood.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("BloodTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { data.enabled = false; data.ground.enabled = false; return; }   // no file: V3 numbers, no ground splats
        try
        {
            var dd = JsonUtility.FromJson<Data>(json); if (dd == null) return;
            if (dd.weights == null) dd.weights = new Mul[0]; if (dd.locations == null) dd.locations = new Mul[0]; if (dd.victims == null) dd.victims = new Victim[0]; if (dd.ground == null) dd.ground = new GroundCfg();
            if (dd.ground.color == null || dd.ground.color.Length < 3) dd.ground.color = new float[] { 0.11f, 0.008f, 0.008f };
            data = dd;
        }
        catch (Exception ex) { Debug.LogError("BloodTable: parse failed: " + ex.Message); }
    }
}
