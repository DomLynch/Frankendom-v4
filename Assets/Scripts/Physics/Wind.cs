using System;
using System.IO;
using UnityEngine;

// WIND v1 (fk_windcode, 20 Nov 2026). NOT APPLIED. UNPROVEN until compiled by the Unity dev. PRESENTATION ONLY. COMPILE-ONLY: a static class, no MonoBehaviour, no scene object, no serialized field,
// no [RuntimeInitializeOnLoadMethod]. It is only ever called from code that is already attached or already booted (SecondaryMotion.Step, ZoneMotes.Make).
// What: one wind per zone from wind.json (found with DataPath.Find, hot reloaded by write time every pollSeconds): world yaw dirDeg (0 = +z, same convention as Foliage windYawDeg), speedMps,
//   a slow Perlin gust on the speed (never a strobe). Zones are the zone_motes.json ids with the same z bands.
// Switch: wind.json "enabled": false (shipped) or a missing file = Vec() is Vector3.zero and nothing else happens. Readers also have their own gain (secondary_motion.json windGain, wind.json moteGain), both 0 by default.
// Reads no sim state, no RNG, no Physics.*; Time.time only for the gust. The simhash cannot change.
public static class Wind
{
    [Serializable] public class Row
    {
        public string id = "";                      // zone_motes.json row id (pit_yard, sobek_reach, hub_camp, grendel, z5_humbaba, z6_erlking, z7_market2)
        public float zFrom = -100000f, zTo = 100000f;   // world z band, [zFrom, zTo)
        public float dirDeg = 70f;                  // direction the air moves TOWARD, world yaw degrees (0 = +z, 90 = +x)
        public float speedMps = 0f;                 // mean speed, metres per second
        public float gustAmp = -1f, gustPeriodS = -1f;   // per row override of the file values (-1 = use the file value)
    }
    [Serializable] public class Cfg
    {
        public string note = "";
        public bool enabled = false, log = true;
        public float gustAmp = 0.5f, gustPeriodS = 7f;   // speed x (1 + gustAmp x (perlin - 0.5)); 0.5 gives +-25 percent
        public float pollSeconds = 2f;
        public float moteGain = 0f;                 // ZoneMotes: wind vector x moteGain is added to a row's drift when the row is built (0 = motes unchanged)
        public Row[] rows = new Row[0];
    }

    const string FileName = "wind.json";
    const float GustSeed = 0.37f, MinPeriod = 0.1f, MinPoll = 0.5f;
    static Cfg cfg; static long stamp = -1; static float nextPoll;

    // true when wind.json says enabled (loads it on first use)
    public static bool Active { get { Poll(); return cfg != null && cfg.enabled; } }

    /// <summary>World wind velocity (m/s) at a world position: zone by pos.z, gusting. Vector3.zero when off.</summary>
    public static Vector3 Vec(Vector3 pos)
    {
        Poll(); if (cfg == null || !cfg.enabled) return Vector3.zero;
        var r = Pick(pos.z); if (r == null || r.speedMps <= 0f) return Vector3.zero;
        float amp = r.gustAmp >= 0f ? r.gustAmp : cfg.gustAmp, per = Mathf.Max(MinPeriod, r.gustPeriodS > 0f ? r.gustPeriodS : cfg.gustPeriodS);
        float gust = 1f + amp * (Mathf.PerlinNoise(Time.time / per, GustSeed) - 0.5f);
        return Dir(r.dirDeg) * (r.speedMps * Mathf.Max(0f, gust));
    }

    /// <summary>Steady (no gust) wind of a zone_motes row id times wind.json moteGain, for ZoneMotes.drift. Vector3.zero when off or moteGain 0.</summary>
    public static Vector3 MoteDrift(string id)
    {
        Poll(); if (cfg == null || !cfg.enabled || cfg.moteGain <= 0f || string.IsNullOrEmpty(id) || cfg.rows == null) return Vector3.zero;
        foreach (var r in cfg.rows) if (r != null && r.id == id) return Dir(r.dirDeg) * (r.speedMps * cfg.moteGain);
        return Vector3.zero;
    }

    static Vector3 Dir(float yawDeg) { float a = yawDeg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)); }

    static Row Pick(float z)
    {
        if (cfg.rows == null) return null;
        foreach (var r in cfg.rows) if (r != null && z >= r.zFrom && z < r.zTo) return r;
        return null;
    }

    static void Poll()
    {
        float now = Time.unscaledTime; if (now < nextPoll) return;
        nextPoll = now + Mathf.Max(MinPoll, cfg != null ? cfg.pollSeconds : 2f);
        string path = DataPath.Find(FileName);
        long st = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
        if (st == stamp) return;
        stamp = st; cfg = null;
        if (path != null)
        {
            try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(path)); }
            catch (Exception e) { Debug.LogWarning("[Wind] bad " + FileName + ": " + e.Message); }
        }
        if (cfg != null && cfg.rows == null) cfg.rows = new Row[0];
        if (cfg == null ? path != null : cfg.log) Debug.Log("[Wind] loaded " + (cfg == null ? "FAILED (off)" : "enabled=" + cfg.enabled + " rows=" + cfg.rows.Length + " moteGain=" + cfg.moteGain));
    }
}
