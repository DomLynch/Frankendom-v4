using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// LOCKFIX v103: ONE helper for every Editor build-time write that lands in Assets/ (masks, generated textures, StreamingAssets/Data copies, props layout).
// Why: the v103 build died 6/6 with IOException Win32 1224 ("file has a user-mapped section open") on File.WriteAllBytes / File.Copy: some other process maps the file,
// and opening an existing file for write with truncation then fails. Windows refuses to TRUNCATE or DELETE a mapped file, but it allows READ with share-read/write and (normally) RENAME.
// What it does, in order, per call:
//   1. skip-if-identical: the file already holds exactly these bytes -> nothing is touched (no lock risk, no mtime change, no git/poller rehash). The bytes on disk are the same either way.
//   2. write the bytes to a sibling temp file (name ends with "~": Unity ignores such files) and replace the target (File.Replace),
//   3. if the replace is refused: move the old file aside (sibling "*.stale~"), move the temp into place,
//   4. optional last resort (setting, off by default): overwrite in place without truncation when the new file is not shorter.
//   A failed attempt that is a lock/sharing error is retried with exponential backoff (bounded by attempts AND total ms). Any other error throws at once. Exhausted -> throws (default) or warns.
// The bytes written are exactly the caller's bytes (File.WriteAllBytes / WriteAllText = UTF-8 without BOM / WriteAllLines = UTF-8 without BOM + Environment.NewLine after every line / File.Copy content):
// no re-encoding, no compression change, no quality change. All settings live in Tools/build_config.json under "io" (missing file or key = the defaults below). Editor only.
public static class SafeIO
{
    [Serializable] public class IoSec
    {
        public int attempts = 8, initialDelayMs = 50, maxDelayMs = 2000, maxTotalMs = 20000, copyMtimeFromSource = 1;
        public float backoffFactor = 2f, jitterPct = 0.25f;
        public int[] retryWin32 = { 1224, 32, 33, 5 };            // 1224 user-mapped section, 32 sharing violation, 33 lock violation, 5 access denied (AV scan / transient)
        public bool skipIfIdentical = true, viaTemp = true, renameAside = true, inPlaceFallback = false, logRetries = true;
        public string tempSuffix = "~", asideSuffix = ".stale~", onExhausted = "throw";   // onExhausted: "throw" | "warn" (warn keeps the old file and logs)
    }
    [Serializable] class Root { public IoSec io = new IoSec(); }

    public const string ConfigPath = "Tools/build_config.json";
    public static int Skipped, Written, Retried, Exhausted;

    static IoSec cfg;
    public static IoSec Settings
    {
        get
        {
            if (cfg != null) return cfg;
            var r = new Root();
            try { if (File.Exists(ConfigPath)) UnityEngine.JsonUtility.FromJsonOverwrite(File.ReadAllText(ConfigPath).TrimStart((char)0xFEFF), r); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("SafeIO: " + ConfigPath + " unreadable, defaults: " + e.Message); }
            if (r.io == null) r.io = new IoSec();
            if (r.io.retryWin32 == null) r.io.retryWin32 = new int[0];
            if (r.io.attempts < 1) r.io.attempts = 1;
            if (string.IsNullOrEmpty(r.io.tempSuffix)) r.io.tempSuffix = "~";
            if (string.IsNullOrEmpty(r.io.asideSuffix)) r.io.asideSuffix = ".stale~";
            return cfg = r.io;
        }
    }
    public static void Reload() { cfg = null; }

    // ---- public API (same argument order as System.IO.File) ----
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var c = Settings;
        if (c.skipIfIdentical && SameContent(path, bytes)) { Skipped++; return; }
        if (Run("write", path, delegate { WriteOnce(path, bytes, c); })) Written++;
    }

    public static void WriteAllText(string path, string text) { WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? "")); }

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var sb = new StringBuilder(); foreach (var l in lines) sb.Append(l).Append(Environment.NewLine);
        WriteAllText(path, sb.ToString());
    }

    /// <summary>Same contract as File.Copy(src, dst, overwrite): content copy; with overwrite false and an existing dst it throws like File.Copy.</summary>
    public static void Copy(string src, string dst, bool overwrite)
    {
        if (!overwrite && File.Exists(dst)) { File.Copy(src, dst, false); return; }
        byte[] b = null;
        Run("read", src, delegate { b = ReadAllBytes(src); });
        if (b == null) return;
        WriteAllBytes(dst, b);
        if (Settings.copyMtimeFromSource != 0) { try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); } catch (Exception) { } }   // File.Copy keeps the source mtime; harmless if refused
    }

    /// <summary>Read that tolerates other readers and writers (share read+write+delete), unlike File.ReadAllBytes (share read only).</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var b = new byte[fs.Length]; int o = 0; while (o < b.Length) { int n = fs.Read(b, o, b.Length - o); if (n <= 0) break; o += n; }
            return b;
        }
    }

    public static string Summary() { return "SAFEIO skipped(identical)=" + Skipped + " written=" + Written + " retries=" + Retried + " exhausted=" + Exhausted; }

    // ---- internals ----
    static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            var fi = new FileInfo(path); if (!fi.Exists || fi.Length != bytes.Length) return false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[81920]; int pos = 0;
                while (pos < bytes.Length)
                {
                    int n = fs.Read(buf, 0, Math.Min(buf.Length, bytes.Length - pos)); if (n <= 0) return false;
                    for (int i = 0; i < n; i++) if (buf[i] != bytes[pos + i]) return false;
                    pos += n;
                }
                return fs.ReadByte() < 0;
            }
        }
        catch (Exception) { return false; }   // cannot compare -> write (the retry loop deals with a real lock)
    }

    static void WriteOnce(string path, byte[] bytes, IoSec c)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bool exists = File.Exists(path);
        if (!c.viaTemp) { File.WriteAllBytes(path, bytes); return; }
        string tmp = path + "." + Process.GetCurrentProcess().Id + c.tempSuffix;
        try
        {
            File.WriteAllBytes(tmp, bytes);                          // a fresh file: nobody maps it
            if (!exists) { File.Move(tmp, path); return; }
            try { File.Replace(tmp, path, null); return; }
            catch (Exception e1)
            {
                if (!IsTransient(e1)) throw;
                if (c.renameAside)
                {
                    string aside = path + c.asideSuffix;
                    try { if (File.Exists(aside)) File.Delete(aside); } catch (Exception) { }
                    string use = File.Exists(aside) ? path + "." + DateTime.UtcNow.Ticks + c.asideSuffix : aside;
                    bool moved = false;
                    try { File.Move(path, use); moved = true; } catch (Exception e2) { if (!IsTransient(e2)) throw; }
                    if (moved)
                    {
                        try { File.Move(tmp, path); try { File.Delete(use); } catch (Exception) { } return; }
                        catch (Exception) { try { if (!File.Exists(path)) File.Move(use, path); } catch (Exception) { } throw; }   // put the old file back
                    }
                }
                if (c.inPlaceFallback && new FileInfo(path).Length <= bytes.Length)
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
                    return;
                }
                throw;
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
    }

    public static bool IsTransient(Exception e)
    {
        if (!(e is IOException) && !(e is UnauthorizedAccessException)) return false;
        string m = e.Message ?? "";
        if (m.IndexOf("user-mapped section", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("returned 1224", StringComparison.Ordinal) >= 0) return true;
        int code = e.HResult & 0xFFFF;
        foreach (var k in Settings.retryWin32) if (k == code) return true;
        if (e is UnauthorizedAccessException) return Array.IndexOf(Settings.retryWin32, 5) >= 0;
        return m.IndexOf("Sharing violation", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // true when the action completed
    static bool Run(string what, string path, Action a)
    {
        var c = Settings; var sw = Stopwatch.StartNew(); double delay = Math.Max(1, c.initialDelayMs); var rng = new System.Random(path.GetHashCode());
        for (int i = 1; ; i++)
        {
            try { a(); return true; }
            catch (Exception e)
            {
                if (!IsTransient(e)) throw;
                bool last = i >= c.attempts || sw.ElapsedMilliseconds + delay > c.maxTotalMs;
                if (last)
                {
                    Exhausted++;
                    string msg = "SafeIO: " + what + " " + path + " still locked after " + i + " attempts / " + sw.ElapsedMilliseconds + " ms: " + e.Message;
                    if (string.Equals(c.onExhausted, "warn", StringComparison.OrdinalIgnoreCase)) { UnityEngine.Debug.LogWarning(msg + " (kept the old file)"); return false; }
                    throw new IOException(msg, e);
                }
                Retried++;
                int wait = (int)Math.Min(c.maxDelayMs, delay * (1.0 + c.jitterPct * (rng.NextDouble() * 2.0 - 1.0)));
                if (c.logRetries) UnityEngine.Debug.LogWarning("SafeIO: " + what + " " + path + " locked (attempt " + i + "/" + c.attempts + "), retry in " + wait + " ms: " + e.Message);
                Thread.Sleep(Math.Max(1, wait));
                delay = Math.Min(c.maxDelayMs, delay * Math.Max(1f, c.backoffFactor));
            }
        }
    }
}
