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
    /// <summary>ZONESAVE v2: true = the file is on disk (or nothing is allowed to be written: RealSaveBlocked). false = the write failed; ZoneSave keeps its dirty flag and retries.</summary>
    bool Save(ZoneFile f);
}

public sealed class LocalFileStore : IZoneStore
{
    public string Path_ { get { return Path.Combine(SavePaths.Dir, ZoneData.Get().persistence.file); } }
    static ZoneFile Parse(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length < 2 || t[0] != '{' || t[t.Length - 1] != '}') return null;   // ZONESAVE v2: JsonUtility is lenient with a cut-off file; a truncated write must count as corrupt
        try { return JsonUtility.FromJson<ZoneFile>(t); } catch (Exception) { return null; }
    }
    public ZoneFile Load()
    {
        string p = Path_;
        try
        {
            // ZONESAVE v2: same order as SaveSlot / LootSave: SafeFile.Recover first (interrupted write, missing primary + .bak), a corrupt primary is quarantined and the .bak used; neither = fresh file.
            string src; string text = SafeFile.ReadValid(p, t => Parse(t) != null, !RunMode.RealSaveBlocked /* FIX2 v1: blocked harness = read only */, out src);
            if (text == null) { if (File.Exists(p) || File.Exists(SafeFile.Bak(p))) { ZoneSave.LastError = "unreadable"; Debug.LogWarning("[ZoneSave] unreadable and no usable backup, starting fresh"); } return new ZoneFile(); }
            var f = Parse(text);
            if (f == null) throw new Exception("empty");
            if (src == "backup") Debug.LogWarning("[ZoneSave] loaded from the backup copy");
            if (f.version > ZoneData.Get().persistence.version)
            {
                string bak = p + ".v" + f.version + ".bak"; if (!RunMode.RealSaveBlocked /* FIX2 v1 */ && !File.Exists(bak)) File.Copy(p, bak);
                Debug.LogWarning("[ZoneSave] file is version " + f.version + ", this build writes " + ZoneData.Get().persistence.version + ": backed up to " + bak + ", starting fresh");
                return new ZoneFile();
            }
            return f;
        }
        catch (Exception e)
        {
            ZoneSave.LastError = e.Message; Debug.LogWarning("[ZoneSave] unreadable (" + e.Message + "), starting fresh");
            return new ZoneFile();
        }
    }
    public bool Save(ZoneFile f)
    {
        if (RunMode.RealSaveBlocked) return true;   // FIX2 v1: second guard at the file layer (any caller, also a custom IZoneStore chain); nothing to write is not a failure
        string p = Path_;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            SafeFile.AtomicWrite(p, JsonUtility.ToJson(f, true));   // FIXSAVE v1: tmp file then File.Replace, previous good copy kept as .bak
            return true;
        }
        catch (Exception e) { ZoneSave.LastError = e.Message; ZoneSave.WriteFailed(e.Message); return false; }
    }
}

public static class ZoneSave
{
    public static string LastError = "";
    // ZONESAVE v2: write-failure state for a HUD / menu to read (no UI here). Statics only.
    public static bool WriteFailing { get; private set; }            // true from the first failed write until one succeeds
    public static string WriteFailReason { get; private set; }       // last failure message
    public static int WriteFailCount { get; private set; }           // failed attempts in a row
    public static float RetryDelay { get { return WriteFailing ? Mathf.Min(30f, Mathf.Pow(2f, Mathf.Min(WriteFailCount, 5))) : 0f; } }   // seconds: 2, 4, 8, 16, 30 ...
    static float nextRetryAt;
    internal static void WriteFailed(string reason)
    {
        bool first = !WriteFailing || WriteFailReason != reason;
        WriteFailing = true; WriteFailReason = reason; WriteFailCount++; nextRetryAt = Time.unscaledTime + RetryDelay;
        if (first) Debug.LogWarning("[ZoneSave] write FAILED (" + reason + "), will retry");   // one line per episode (and again only if the reason changes), not one per retry
    }
    static void ClearFailure(bool log) { if (WriteFailing && log) Debug.Log("[ZoneSave] write recovered after " + WriteFailCount + " failed attempt(s)"); WriteFailing = false; WriteFailReason = ""; WriteFailCount = 0; nextRetryAt = 0f; }
    public static IZoneStore Store = new LocalFileStore();
    static ZoneFile data; static bool dirty;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; dirty = false; LastError = ""; ClearFailure(false); Store = new LocalFileStore(); }

    public static float NewGamePendingUntil;   // SAVEFIX v2: set by OnNewGame, cleared by ZoneRuntime.Rebind, expires after one save interval: the restart is under way, no autosave may write the old zone back
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetSaveFix() { NewGamePendingUntil = 0f; }   // SAVEFIX v2
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)] static void HookErase() { SaveSlot.OnErase += OnNewGame; }   // SAVEFIX v2: same hook point as LootSave (SaveSlot.Erase only fires it while SaveSlot.Active)
    static void OnNewGame() { var P = ZoneData.Get().persistence; if (!P.resetOnNewGame || !Active) return; data = new ZoneFile(); Fix(data); dirty = false; ClearFailure(false); /* ZONESAVE v2: a pending retry must never write the old zone back */ NewGamePendingUntil = Time.unscaledTime + P.saveEverySeconds; var ls = Store as LocalFileStore; if (ls != null && !RunMode.RealSaveBlocked) { try { string p = ls.Path_; if (File.Exists(p)) { SafeFile.Quarantine(p, "erased", "was erased by New game"); File.Delete(p); } SafeFile.Discard(p); /* .bak and .tmp too */ } catch (Exception e) { LastError = e.Message; } } Debug.Log("[SaveFix] zones save reset by New game"); }   // SAVEFIX v2
    /// <summary>True = ZoneRuntime.SaveNow must not write now. Both rows are off by default (then always false, no log).</summary>   // SAVEFIX v2
    public static bool ShouldSkipSave(bool down) { var P = ZoneData.Get().persistence; if (P.skipSaveWhileDown && down) { Debug.Log("[SaveFix] zone save skipped while down: the last living record stays"); return true; } if (P.resetOnNewGame && Time.unscaledTime < NewGamePendingUntil) { Debug.Log("[SaveFix] zone save skipped: New game restart pending"); return true; } return false; }   // SAVEFIX v2
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
    /// <summary>ZONESAVE v2: dirty stays set until a write SUCCEEDED. After a failure a non-forced call waits for the backoff (RetryDelay); a forced call (gate, quit, autosave) always tries. Returns true when nothing is pending afterwards.</summary>
    public static bool Flush(bool force = false)
    {
        if (!Active || RunMode.RealSaveBlocked || (!dirty && !force)) return true;   // FIX2 v1: a harness run that did not redirect its saves (no -savePath) never writes the real zones_save.json; the state stays in memory like LootSave
        if (!force && WriteFailing && Time.unscaledTime < nextRetryAt) return false;
        Data.savedUtc = DateTime.UtcNow.ToString("o");
        if (Store.Save(Data)) { dirty = false; ClearFailure(true); return true; }
        dirty = true; if (!WriteFailing) WriteFailed("store refused the write"); return false;   // a custom store that returned false without calling WriteFailed
    }
}
