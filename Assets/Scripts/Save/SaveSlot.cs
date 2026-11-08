using System;
using System.IO;
using UnityEngine;

// REBIND v100 (lane laneREBIND): the one save slot. File: Application.persistentDataPath/<save.json file> (default save_slot1.json).
//   { "version": 1, "savedUtc": "...", "ladder": { "row": 2, "rowId": "pit_champion", "fightNumber": 3, "victories": 2 }, "loot": { "file": "loot_save.json", "lootVersion": 1, "owned": 3, "worn": 2 } }
// ladder = how far the run got (opponents.json row index + id, the FIGHT n counter, wins). It only moves forward: a victory saves the NEXT row (what the Next button would start), a Retry or a replay never
//          lowers it, so quitting at the victory screen resumes at the next opponent. At boot GameLoop's first fight starts on that row (SaveSlot.ApplyLadder, called by GameLoopDriver.BeginFight).
// loot   = a POINTER + summary, not a copy: the carried items and the worn gear stay in laneLOOTUI's own file (LootSave, loot_ui.json save.file, default loot_save.json), which writes itself after every pickup / equip.
//          This slot records the file name, its schema version and the counts each time the slot is written (LootSave calls SaveSlot.LootChanged), so one look at the slot says what the loot file should hold.
// Harness runs (MenuCfg.Harness: batch mode, -balanceauto, -looptest ...) neither read nor write it. Data: save.json (enabled, version, file, resume.ladder). A newer file version is backed up, never silently overwritten.
public static class SaveSlot
{
    [Serializable] public class LadderS { public int row = 0, fightNumber = 1, victories = 0; public string rowId = ""; }
    [Serializable] public class LootS { public string file = ""; public int lootVersion = 0, owned = 0, worn = 0; }
    [Serializable] public class FileData { public int version = 1; public string savedUtc = ""; public LadderS ladder = new LadderS(); public LootS loot = new LootS(); }
    [Serializable] class ResumeC { public bool ladder = true; }
    [Serializable] class CfgC { public bool enabled = true, debugLog = false; public int version = 1, maxClearedRow = 1000; public string file = "save_slot1.json", lootDataFile = "loot_ui.json", lootFallbackFile = "loot_save.json"; public ResumeC resume = new ResumeC(); }
    [Serializable] class LootUiPeek { public LootUiSave save = new LootUiSave(); }
    [Serializable] class LootUiSave { public string file = ""; }
    [Serializable] class LootPeekRow { public string slot = "", item = ""; }
    [Serializable] class LootPeek { public int version = 0; public string[] owned = new string[0]; public LootPeekRow[] equipped = new LootPeekRow[0]; }

    static CfgC cfg; static FileData data = new FileData(); static bool loaded, applied;
    public static string LastError = "";
    /// <summary>LootSave subscribes: the slot was erased (New game), forget the carried items and delete the loot file.</summary>
    public static event Action OnErase;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; data = new FileData(); loaded = false; applied = false; LastError = ""; OnErase = null; }

    static CfgC Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new CfgC(); string txt = HudCfg.ReadData("save.json");
        if (txt != null) { try { var c = JsonUtility.FromJson<CfgC>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[SaveSlot] save.json unreadable, defaults used: " + e.Message); } }
        if (cfg.resume == null) cfg.resume = new ResumeC(); if (string.IsNullOrEmpty(cfg.file)) cfg.file = "save_slot1.json"; cfg.version = Mathf.Max(1, cfg.version);
        return cfg;
    }
    /// <summary>The slot is in use: data enabled and not a harness run.</summary>
    public static bool Active { get { return Cfg().enabled && !MenuCfg.Harness && !RunMode.RealSaveBlocked /* FIXSAVE v1: a harness run that did not redirect its saves never touches the real slot */; } }
    public static string FilePath { get { return Path.Combine(SavePaths.Dir, Cfg().file); } }
    public static FileData Data { get { EnsureLoaded(); return data; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] static void Boot() { if (Active) EnsureLoaded(); }

    // ---------------- load ----------------
    static void EnsureLoaded()
    {
        if (loaded) return; loaded = true; data = new FileData();
        if (!Active) return;
        string p = FilePath;
        SafeFile.Recover(p);   // FIXSAVE v1: an interrupted old Delete + Move left only the .tmp
        try
        {
            if (!File.Exists(p)) return;
            string txt = File.ReadAllText(p).TrimStart((char)0xFEFF);
            var f = JsonUtility.FromJson<FileData>(txt);
            if (f == null) throw new Exception("empty");
            if (f.version > Cfg().version)
            {
                string bak = p + ".v" + f.version + ".bak"; if (!File.Exists(bak)) File.Copy(p, bak);
                Debug.LogWarning("[SaveSlot] save file is version " + f.version + ", this build writes " + Cfg().version + ": backed up to " + bak + ", starting a fresh slot");
                return;
            }
            Migrate(f);
            if (f.ladder == null) f.ladder = new LadderS(); if (f.loot == null) f.loot = new LootS();
            f.ladder.row = Mathf.Clamp(f.ladder.row, 0, Cfg().maxClearedRow); f.ladder.fightNumber = Mathf.Max(1, f.ladder.fightNumber); f.ladder.victories = Mathf.Max(0, f.ladder.victories);
            data = f;
            if (Cfg().debugLog) Debug.Log("[SaveSlot] loaded " + p + ": row " + data.ladder.row + " (" + data.ladder.rowId + ") fight " + data.ladder.fightNumber + " wins " + data.ladder.victories);
        }
        catch (Exception e)
        {
            LastError = e.Message; Debug.LogWarning("[SaveSlot] save unreadable (" + e.Message + "), starting a fresh slot");
            SafeFile.Quarantine(p, "bad", "unreadable (" + e.Message + ")", true);   // FIXSAVE v1: timestamped, last 3 kept (was one .bad that the next bad file deleted)
            data = new FileData();
        }
    }
    /// <summary>Older versions step up here, one case per version (there is only version 1 so far).</summary>
    static void Migrate(FileData f)
    {
        // if (f.version < 2) { ...fill new fields...; f.version = 2; }
        f.version = Cfg().version;
    }

    // ---------------- ladder ----------------
    /// <summary>GameLoopDriver.BeginFight on the first fight of a run: start on the saved row / fight number.</summary>
    public static void ApplyLadder()
    {
        if (applied || !Active || !GameLoop.LadderOn || !Cfg().resume.ladder) return;
        applied = true; EnsureLoaded();
        if (data.ladder.row <= 0 && data.ladder.fightNumber <= 1) return;   // no progress saved
        int row = data.ladder.row;
        var t = GameLoop.Opponents();
        if (!string.IsNullOrEmpty(data.ladder.rowId) && t.rows != null && row < t.rows.Length)   // the row id wins when opponents.json was reordered / extended since (past the last row = endless fights: the index stays)
            for (int i = 0; i < t.rows.Length; i++) if (t.rows[i] != null && t.rows[i].id == data.ladder.rowId) { row = i; break; }
        GameLoop.OpponentIndex = Mathf.Max(row, GameLoop.OpponentIndex); GameLoop.FightNumber = Mathf.Max(data.ladder.fightNumber, GameLoop.FightNumber);
        if (Cfg().debugLog) Debug.Log("[SaveSlot] resumed at row " + GameLoop.OpponentIndex + " fight " + GameLoop.FightNumber);
    }

    /// <summary>A fight began (BeginFight): raise the saved position when the run moved past it. Never lowers it.</summary>
    public static void FightStarted(int row, int fightNumber)
    {
        if (!Active || !GameLoop.LadderOn) return; EnsureLoaded();
        if (row <= data.ladder.row && fightNumber <= data.ladder.fightNumber && (data.ladder.rowId.Length > 0 || row == 0 && fightNumber <= 1)) return;
        Raise(row, fightNumber); Write();
    }
    /// <summary>The player won the fight on this row: the slot now points at the next row (what Next starts).</summary>
    public static void Victory(int row, int fightNumber)
    {
        if (!Active || !GameLoop.LadderOn) return; EnsureLoaded();
        data.ladder.victories++; Raise(row + 1, fightNumber + 1); Write();
    }
    static void Raise(int row, int fightNumber)
    {
        row = Mathf.Clamp(row, 0, Cfg().maxClearedRow);
        if (row > data.ladder.row || data.ladder.rowId.Length == 0) { data.ladder.row = Mathf.Max(row, data.ladder.row); data.ladder.rowId = RowId(data.ladder.row); }
        if (fightNumber > data.ladder.fightNumber) data.ladder.fightNumber = fightNumber;
    }
    static string RowId(int index)
    {
        var t = GameLoop.Opponents(); if (t.rows == null || t.rows.Length == 0) return "";
        var r = t.rows[Mathf.Clamp(index, 0, t.rows.Length - 1)]; return r != null && r.id != null ? r.id : "";
    }

    // ---------------- loot pointer ----------------
    /// <summary>LootSave wrote its file: refresh the pointer + counts in the slot.</summary>
    public static void LootChanged() { if (!Active) return; EnsureLoaded(); Write(); }

    static string LootFileName()
    {
        string n = ""; string txt = HudCfg.ReadData(Cfg().lootDataFile);
        if (txt != null) { try { var l = JsonUtility.FromJson<LootUiPeek>(txt); if (l != null && l.save != null) n = l.save.file; } catch { } }
        return string.IsNullOrEmpty(n) ? Cfg().lootFallbackFile : n;
    }
    static void PeekLoot()
    {
        var L = data.loot; L.file = LootFileName(); L.lootVersion = 0; L.owned = 0; L.worn = 0;
        try
        {
            string p = Path.Combine(SavePaths.Dir, L.file); if (!File.Exists(p)) return;
            var lp = JsonUtility.FromJson<LootPeek>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (lp == null) return;
            L.lootVersion = lp.version; L.owned = lp.owned != null ? lp.owned.Length : 0;
            if (lp.equipped != null) foreach (var r in lp.equipped) if (r != null && !string.IsNullOrEmpty(r.item)) L.worn++;
        }
        catch (Exception e) { LastError = e.Message; }
    }

    // ---------------- write / erase ----------------
    static void Write()
    {
        if (!Active) return;
        try
        {
            PeekLoot(); data.version = Cfg().version; data.savedUtc = DateTime.UtcNow.ToString("o");
            string p = FilePath, dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SafeFile.AtomicWrite(p, JsonUtility.ToJson(data, true)); LastError = "";   // FIXSAVE v1: tmp + File.Replace (+ .bak), was Delete then Move: a crash between left no slot file
            if (Cfg().debugLog) Debug.Log("[SaveSlot] saved " + p + " row " + data.ladder.row + " fight " + data.ladder.fightNumber);
        }
        catch (Exception e) { LastError = e.Message; Debug.LogWarning("[SaveSlot] save failed: " + e.Message); }
    }

    /// <summary>New game: forget the ladder progress, delete the slot file, tell LootSave to forget the carried items. The caller restarts the fight.</summary>
    public static void Erase()
    {
        GameLoop.OpponentIndex = Mathf.Max(0, GameLoop.Opponents().startIndex); GameLoop.FightNumber = 1;   // the run restarts even when the slot is switched off
        if (!Active) return; EnsureLoaded();
        data = new FileData(); applied = true;
        try { string p = FilePath; if (File.Exists(p)) { SafeFile.Quarantine(p, "erased", "was erased by New game"); File.Delete(p); }
            SafeFile.Discard(p); /* ERASEFIX v1: .bak/.tmp too, else Recover restores the erased save */ } catch (Exception e) { LastError = e.Message; }   // FIXSAVE v1: last 3 erased slots stay as .erased.<stamp>
        try { if (OnErase != null) OnErase(); } catch (Exception e) { Debug.LogWarning("[SaveSlot] erase hook failed: " + e.Message); }
        if (Cfg().debugLog) Debug.Log("[SaveSlot] slot erased");
    }
}
