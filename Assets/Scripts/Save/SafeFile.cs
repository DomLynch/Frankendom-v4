using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// FIXSAVE v1 (laneFIXSAVE): crash-safe file helpers for the save files. No UnityEngine dependency when FIXSAVE_NOUNITY is defined (the test build), so laneFIXSAVE\test\SaveTests.cs compiles and runs this file as is.
//   AtomicWrite   tmp file (flushed to disk) then File.Replace(tmp, path, path + ".bak"): at every instant the path holds the complete old or the complete new text, and the previous good copy stays as .bak.
//                 (Was: File.Delete(path); File.Move(tmp, path) in SaveSlot = a crash between the two lines left NO file.)
//   Recover       the file is missing but a finished .tmp (old Delete + Move crash) or a .bak exists: put it back before the loader reads.
//   Quarantine    corrupt / newer-version / about-to-be-erased file: keep a timestamped copy (path.bad.20261108T101500123, last 3 kept) BEFORE any reset overwrites it.
//   WarnOnce      the log line for all of the above and for DataPath.ReadText: once per key, hard cap per session, so a broken file cannot flood the log.
public static class SafeFile
{
    public const int KeepDefault = 3;
    public static Func<DateTime> Now = () => DateTime.UtcNow;
    public static Action<string> Sink;       // tests: capture log lines; default = UnityEngine.Debug.LogWarning
    public static Action<string> FaultAt;    // tests: throw from here at "tmp-written" / "replaced" to simulate a crash at that step
    public static int WarnCap = 32;
    static readonly HashSet<string> warned = new HashSet<string>(); static int warnCount;

#if !FIXSAVE_NOUNITY
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { warned.Clear(); warnCount = 0; }
#endif
    public static void ResetWarnings() { warned.Clear(); warnCount = 0; }

    /// <summary>Log `msg` once per `key` (per session); after WarnCap distinct keys nothing more is logged. True when it was written.</summary>
    public static bool WarnOnce(string key, string msg)
    {
        if (key == null) key = msg ?? "";
        lock (warned)   // DataPath.ReadText is also called from worker threads (MonsterLook)
        {
            if (warned.Contains(key)) return false;
            if (warnCount >= WarnCap) return false;
            warned.Add(key); warnCount++;
        }
        if (Sink != null) Sink(msg);
#if !FIXSAVE_NOUNITY
        else UnityEngine.Debug.LogWarning(msg);
#else
        else Console.Error.WriteLine(msg);
#endif
        return true;
    }

    public static string Tmp(string path) { return path + ".tmp"; }
    public static string Bak(string path) { return path + ".bak"; }

    /// <summary>Write `text` so the file is never missing and never half written. Throws on failure (callers keep their own catch).</summary>
    public static void AtomicWrite(string path, string text)
    {
        string dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = Tmp(path);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] b = new UTF8Encoding(false).GetBytes(text); fs.Write(b, 0, b.Length); fs.Flush(true);
        }
        if (FaultAt != null) FaultAt("tmp-written");
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, Bak(path)); }
            catch (PlatformNotSupportedException) { File.Replace(tmp, path, null); }   // a file system that cannot keep the backup: still one atomic swap
            catch (IOException) { if (!File.Exists(tmp)) throw; File.Replace(tmp, path, null); }   // the .bak is locked: swap without it rather than lose the save
        }
        else File.Move(tmp, path);
        if (FaultAt != null) FaultAt("replaced");
    }

    /// <summary>The save is gone but a finished .tmp (the old Delete + Move crash) or a .bak is left: restore it so the loader finds a file. True when something was restored.</summary>
    public static bool Recover(string path)
    {
        try
        {
            if (File.Exists(path)) return false;
            string tmp = Tmp(path), bak = Bak(path);
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) { File.Move(tmp, path); WarnOnce("recover:" + path, "[Save] " + Path.GetFileName(path) + " was missing (interrupted write): restored from " + Path.GetFileName(tmp)); return true; }
            if (File.Exists(bak) && new FileInfo(bak).Length > 0) { File.Copy(bak, path); WarnOnce("recover:" + path, "[Save] " + Path.GetFileName(path) + " was missing: restored from " + Path.GetFileName(bak)); return true; }
        }
        catch (Exception e) { WarnOnce("recoverfail:" + path, "[Save] could not recover " + path + ": " + e.Message); }
        return false;
    }

    /// <summary>Keep a timestamped copy (or move, when `move`) of `path` as path.&lt;tag&gt;.&lt;stamp&gt; and prune to the newest `keep`. An identical newest copy is not duplicated. Returns the copy, or null (no file / failure).</summary>
    public static string Quarantine(string path, string tag, string reason, bool move = false, int keep = KeepDefault)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string dir = Path.GetDirectoryName(path) ?? "", name = Path.GetFileName(path), pre = name + "." + tag + ".";
            string[] have = Directory.GetFiles(dir.Length == 0 ? "." : dir, pre + "*"); Array.Sort(have, StringComparer.Ordinal);
            string dst;
            if (have.Length > 0 && SameBytes(path, have[have.Length - 1])) dst = have[have.Length - 1];
            else
            {
                string stamp = Now().ToString("yyyyMMdd'T'HHmmssfff"); dst = Path.Combine(dir, pre + stamp); int n = 1;
                while (File.Exists(dst)) dst = Path.Combine(dir, pre + stamp + "_" + (n++));
                File.Copy(path, dst);
            }
            if (move) File.Delete(path);
            have = Directory.GetFiles(dir.Length == 0 ? "." : dir, pre + "*"); Array.Sort(have, StringComparer.Ordinal);
            for (int i = 0; i < have.Length - Math.Max(1, keep); i++) { try { File.Delete(have[i]); } catch (Exception) { } }
            WarnOnce("quarantine:" + path + ":" + tag, "[Save] " + name + " " + reason + ": kept a copy as " + Path.GetFileName(dst) + (move ? " and moved it away" : ""));
            return dst;
        }
        catch (Exception e) { WarnOnce("quarantinefail:" + path, "[Save] could not keep a copy of " + path + " (" + reason + "): " + e.Message); return null; }
    }
    static bool SameBytes(string a, string b)
    {
        try { var x = new FileInfo(a); var y = new FileInfo(b); if (x.Length != y.Length) return false; return Convert.ToBase64String(File.ReadAllBytes(a)) == Convert.ToBase64String(File.ReadAllBytes(b)); }
        catch (Exception) { return false; }
    }
}
