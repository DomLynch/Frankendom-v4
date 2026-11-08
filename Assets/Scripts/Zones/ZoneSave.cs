using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ZONES v1: the persistent zone state. ONE file in Application.persistentDataPath (zones.json persistence.file, default zones_save.json), separate from SaveSlot's ladder file and from LootSave's loot file
// (loot and worn gear stay with LootSave exactly as today; nothing here copies them).
//   { "version": 1, "savedUtc": "...", "player": { "zone": "pit_yard", "spot": "arrive_from_sobek", "hpFrac": 0.62 }, "spawns": [ { zone, spawn, creature, generation, killedAtMs, respawnAtMs } ], "gates": [ "gate_pit_to_sobek" ] }
// Names follow the Supabase zone_spawns columns (zone = instance, generation, respawn_at). The storage is behind IZoneStore, so a later SupabaseStore (or a friends' host) replaces LocalFileStore
// without touching ZoneRuntime or ZoneCore. No networking code is in this lane.
// Harness runs (MenuCfg.Harness: batch mode, -balanceauto, -looptest ...) never read or write it. A file with a HIGHER version is backed up, never overwritten.
[Serializable] public class ZonePlayerRec { public string zone = "", spot = ""; public float hpFrac = 1f; public bool has; }
[Serializable] public class ZoneFile { public int version = 1; public string savedUtc = ""; public ZonePlayerRec player = new ZonePlayerRec(); public SpawnRec[] spawns = new SpawnRec[0]; public string[] gates = new string[0]; }

public interface IZoneStore
{
    ZoneFile Load();
    void Save(ZoneFile f);
}

public sealed class LocalFileStore : IZoneStore
{
    public string Path_ { get { return Path.Combine(SavePaths.Dir, ZoneData.Get().persistence.file); } }
    public ZoneFile Load()
    {
        string p = Path_;
        try
        {
            if (!File.Exists(p)) return new ZoneFile();
            var f = JsonUtility.FromJson<ZoneFile>(File.ReadAllText(p).TrimStart((char)0xFEFF));
            if (f == null) throw new Exception("empty");
            if (f.version > ZoneData.Get().persistence.version)
            {
                string bak = p + ".v" + f.version + ".bak"; if (!File.Exists(bak)) File.Copy(p, bak);
                Debug.LogWarning("[ZoneSave] file is version " + f.version + ", this build writes " + ZoneData.Get().persistence.version + ": backed up to " + bak + ", starting fresh");
                return new ZoneFile();
            }
            return f;
        }
        catch (Exception e)
        {
            ZoneSave.LastError = e.Message; Debug.LogWarning("[ZoneSave] unreadable (" + e.Message + "), starting fresh");
            SafeFile.Quarantine(p, "bad", "unreadable (" + e.Message + ")", true);   // FIXSAVE v1: timestamped, last 3 kept
            return new ZoneFile();
        }
    }
    public void Save(ZoneFile f)
    {
        string p = Path_, tmp = p + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            SafeFile.AtomicWrite(p, JsonUtility.ToJson(f, true));   // FIXSAVE v1: tmp file then File.Replace, previous good copy kept as .bak
        }
        catch (Exception e) { ZoneSave.LastError = e.Message; Debug.LogWarning("[ZoneSave] write failed: " + e.Message); }
    }
}

public static class ZoneSave
{
    public static string LastError = "";
    public static IZoneStore Store = new LocalFileStore();
    static ZoneFile data; static bool dirty;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; dirty = false; LastError = ""; Store = new LocalFileStore(); }

    public static bool Active { get { return ZoneData.On && !MenuCfg.Harness; } }
    public static ZoneFile Data { get { if (data == null) { data = Active ? Store.Load() : new ZoneFile(); Fix(data); } return data; } }
    static void Fix(ZoneFile f) { if (f.player == null) f.player = new ZonePlayerRec(); if (f.spawns == null) f.spawns = new SpawnRec[0]; if (f.gates == null) f.gates = new string[0]; f.version = ZoneData.Get().persistence.version; }

    public static SpawnRec Rec(string zone, string spawn)
    {
        foreach (var r in Data.spawns) if (r != null && r.zone == zone && r.spawn == spawn) return r;
        return null;
    }
    public static void Put(SpawnRec n)
    {
        var l = new List<SpawnRec>(Data.spawns); bool done = false;
        for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].zone == n.zone && l[i].spawn == n.spawn) { l[i] = n; done = true; }
        if (!done) l.Add(n); Data.spawns = l.ToArray(); dirty = true;
    }
    public static bool GateUnlocked(string gate) { return Array.IndexOf(Data.gates, gate) >= 0; }
    public static void UnlockGate(string gate) { if (GateUnlocked(gate)) return; var l = new List<string>(Data.gates) { gate }; Data.gates = l.ToArray(); dirty = true; }
    public static void SetPlayer(string zone, string spot, float hpFrac) { var p = Data.player; p.zone = zone; p.spot = spot; p.hpFrac = Mathf.Clamp01(hpFrac); p.has = true; dirty = true; }
    public static void Flush(bool force = false)
    {
        if (!Active || (!dirty && !force)) return;
        Data.savedUtc = DateTime.UtcNow.ToString("o"); Store.Save(Data); dirty = false;
    }
}
