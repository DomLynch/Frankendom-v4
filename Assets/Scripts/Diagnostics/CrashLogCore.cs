using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// RCHYGIENE v100 (lane laneRCHYGIENE): the file side of the release crash logger. NO UnityEngine here on purpose (it is unit-tested outside Unity: _agents/laneRCHYGIENE/tests).
// CrashLog.cs is the Unity glue (log callback, quit, pump); all numbers come from Assets/Data/crash_log.json.
//
// One CrashLogCore = one session file  <dir>/session_<stamp>.log:
//   * a header (version, platform, ...), then every Error / Exception / Assert the glue passes in as an ERROR entry with its (truncated) stack trace;
//   * a RING BUFFER of the last ringLines log lines of ANY level (message only, one line each): lines not written yet are written as a "context" block just before each error
//     and on every heartbeat / flush, so a crash always leaves the lines that led to it, and a chatty log can never grow the file: the ring overwrites, the file has a hard size cap;
//   * identical errors are written maxRepeats times, then only counted ("suppressed" summary at flush / close);
//   * size caps: maxFileBytes per session (after it: entries are counted, not written; a small tail reserve is kept for the closing summary),
//     maxFiles old sessions kept, maxTotalBytes for the whole folder (oldest deleted first).
// The crash flag: running.flag in the same folder is written at start and deleted on a CLEAN close; if it is still there at the next start, the last session did not end cleanly.
// Every public member is thread-safe and never throws (a logger must not be the thing that crashes).
public sealed class CrashLogCore : IDisposable
{
    public struct Opts
    {
        public string dir, stamp, header;
        public int ringLines, maxMessageChars, maxStackChars, maxRepeats, maxFiles;
        public long maxFileBytes, maxTotalBytes, tailReserveBytes;
    }
    public sealed class Previous { public string stamp, logFile; public int pid; public bool wasRunning; }

    public const string MarkerName = "running.flag", FilePrefix = "session_", FileExt = ".log";

    readonly object sync = new object();
    readonly Opts o;
    readonly string[] ring; long ringSeq, ringWrittenSeq;   // ringSeq = lines ever added; ringWrittenSeq = lines already written to the file
    readonly Dictionary<string, int> seen = new Dictionary<string, int>();
    readonly Dictionary<string, int> suppressedBy = new Dictionary<string, int>(); readonly Dictionary<string, string> label = new Dictionary<string, string>();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    StreamWriter w; long written; bool capped, closed;
    public string Path { get; private set; }
    public int ErrorsWritten { get; private set; }
    public int ErrorsSuppressed { get; private set; }
    public int EntriesDropped { get; private set; }   // not written because of the size cap

    public CrashLogCore(Opts opts)
    {
        o = opts; if (o.ringLines < 1) o.ringLines = 1; ring = new string[o.ringLines];
        try
        {
            Directory.CreateDirectory(o.dir);
            Prune(o.dir, o.maxFiles - 1, o.maxTotalBytes);
            string p = System.IO.Path.Combine(o.dir, FilePrefix + o.stamp + FileExt); int n = 2;
            while (File.Exists(p)) p = System.IO.Path.Combine(o.dir, FilePrefix + o.stamp + "_" + (n++) + FileExt);
            Path = p;
            w = new StreamWriter(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)); w.AutoFlush = false;
            Raw(o.header ?? "", true); Raw("", true);
        }
        catch (Exception) { w = null; }
    }

    // ---------------- entries ----------------
    /// <summary>One log message of any level. isError: Error / Exception / Assert (written now, with stack). Others only go into the ring.</summary>
    public void Add(string level, string message, string stack, bool isError, int frame)
    {
        try
        {
            lock (sync)
            {
                if (closed) return;
                string msg = Trunc(Oneline(message), o.maxMessageChars);
                string line = "[" + Stamp(frame) + "] " + level + " " + msg;
                ring[(int)(ringSeq % o.ringLines)] = line; ringSeq++;
                if (!isError) return;

                string key = level + "|" + Trunc(message ?? "", 240) + "|" + Trunc(stack ?? "", 160); int count; seen.TryGetValue(key, out count); seen[key] = ++count;
                if (count > o.maxRepeats)
                {
                    ErrorsSuppressed++; int s; suppressedBy.TryGetValue(key, out s); suppressedBy[key] = s + 1; label[key] = Trunc(msg, 120);
                    // the line stays in the ring but must not be written again as context: mark it written
                    ringWrittenSeq = ringSeq; return;
                }
                if (w == null) { return; }
                WriteContext(true);
                Raw(line, false);
                string st = Trunc((stack ?? "").Replace("\r", ""), o.maxStackChars);
                if (st.Length > 0) Raw("    " + st.Replace("\n", "\n    ").TrimEnd(), false);
                ErrorsWritten++;
                ringWrittenSeq = ringSeq;
                FlushLocked();
            }
        }
        catch (Exception) { }
    }

    /// <summary>Writes the ring lines that are not in the file yet (called every few seconds and before the app is suspended). Cheap when nothing is new.</summary>
    public void Heartbeat() { try { lock (sync) { if (closed || w == null) return; if (ringSeq > ringWrittenSeq) { WriteContext(false); FlushLocked(); } } } catch (Exception) { } }

    /// <summary>Everything to disk now (focus lost, app paused, fatal exception).</summary>
    public void Flush(string reason)
    {
        try
        {
            lock (sync)
            {
                if (closed || w == null) return;
                WriteContext(false); WriteSuppressed();
                if (!string.IsNullOrEmpty(reason)) Raw("[" + Stamp(-1) + "] FLUSH " + reason, false);
                FlushLocked();
            }
        }
        catch (Exception) { }
    }

    /// <summary>End of the session. clean=false is only used when the glue knows the end is not a normal quit.</summary>
    public void Close(bool clean)
    {
        try
        {
            lock (sync)
            {
                if (closed) return;
                if (w != null)
                {
                    WriteContext(false); WriteSuppressed();
                    Raw("", true);
                    Raw("=== session end: " + (clean ? "clean quit" : "NOT clean") + ", " + clock.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s, errors written " + ErrorsWritten + ", suppressed repeats " + ErrorsSuppressed + ", dropped by size cap " + EntriesDropped + " ===", true);
                    FlushLocked(); w.Dispose(); w = null;
                }
                closed = true;
            }
        }
        catch (Exception) { }
    }
    public void Dispose() { Close(true); }

    // ---------------- internals (call with sync held) ----------------
    void WriteContext(bool beforeError)
    {
        if (w == null || ringSeq <= ringWrittenSeq) return;
        long from = Math.Max(ringWrittenSeq, ringSeq - o.ringLines); long lost = from - ringWrittenSeq;
        // when called before an error the newest ring line IS the error: it is written by the caller, so the context stops one line short
        long to = beforeError ? ringSeq - 1 : ringSeq;
        if (to <= from && lost == 0) return;
        Raw("--- context" + (lost > 0 ? " (" + lost + " older lines were overwritten before they could be written)" : "") + " ---", false);
        for (long i = from; i < to; i++) Raw("  " + ring[(int)(i % o.ringLines)], false);
        ringWrittenSeq = to;
    }
    void WriteSuppressed()
    {
        if (suppressedBy.Count == 0) return;
        foreach (var kv in suppressedBy) { string lb; label.TryGetValue(kv.Key, out lb); Raw("  (" + kv.Value + " more of: " + lb + ")", true); }
        suppressedBy.Clear(); label.Clear();
    }
    void Raw(string text, bool reserve)
    {
        if (w == null) return;
        int bytes = Encoding.UTF8.GetByteCount(text) + 2;
        long cap = reserve ? o.maxFileBytes + o.tailReserveBytes : o.maxFileBytes;
        if (written + bytes > cap)
        {
            if (!capped) { capped = true; string m = "[log size cap of " + (o.maxFileBytes / 1024) + " KB reached: later entries are only counted]"; w.WriteLine(m); written += m.Length + 2; }
            EntriesDropped++; return;
        }
        w.WriteLine(text); written += bytes;
    }
    void FlushLocked() { try { w.Flush(); } catch (Exception) { } }
    string Stamp(int frame) { return "+" + clock.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "s" + (frame >= 0 ? " f" + frame : ""); }
    static string Oneline(string s) { return s == null ? "" : s.Replace("\r", " ").Replace("\n", " | "); }
    static string Trunc(string s, int n) { if (s == null) return ""; return s.Length <= n || n <= 0 ? s : s.Substring(0, n) + "...(+" + (s.Length - n) + ")"; }

    // ---------------- folder hygiene ----------------
    /// <summary>Keeps the newest keepFiles session logs and at most maxTotalBytes in the folder (oldest deleted first). Never touches the marker.</summary>
    public static void Prune(string dir, int keepFiles, long maxTotalBytes)
    {
        try
        {
            var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles(FilePrefix + "*" + FileExt));
            files.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));   // newest first (the stamp sorts as text)
            long total = 0; int kept = 0;
            foreach (var f in files)
            {
                bool drop = kept >= Math.Max(0, keepFiles) || (maxTotalBytes > 0 && total + f.Length > maxTotalBytes);
                if (drop) { try { f.Delete(); } catch (Exception) { } } else { kept++; total += f.Length; }
            }
        }
        catch (Exception) { }
    }

    // ---------------- the "last session crashed" marker ----------------
    /// <summary>Reads the marker left by the previous run (null = it ended cleanly or there was none) and writes a new one for this run.
    /// A marker whose process is still alive belongs to a second running copy of the game: it is reported as not crashed and left alone (the new run arms no marker).</summary>
    public static Previous ArmMarker(string dir, string stamp, string logFileName, int pid, Func<int, bool> isAlive)
    {
        Previous prev = null;
        try
        {
            Directory.CreateDirectory(dir); string mp = System.IO.Path.Combine(dir, MarkerName);
            if (File.Exists(mp))
            {
                var lines = File.ReadAllLines(mp); var p = new Previous();
                foreach (var l in lines)
                {
                    int i = l.IndexOf('='); if (i < 0) continue; string k = l.Substring(0, i), v = l.Substring(i + 1);
                    if (k == "stamp") p.stamp = v; else if (k == "log") p.logFile = v; else if (k == "pid") int.TryParse(v, out p.pid);
                }
                if (p.pid > 0 && p.pid != pid && isAlive != null && isAlive(p.pid)) { p.wasRunning = true; return p; }   // a live second copy owns the marker
                prev = p;
            }
            File.WriteAllLines(mp, new[] { "stamp=" + stamp, "log=" + logFileName, "pid=" + pid });
        }
        catch (Exception) { }
        return prev;
    }
    /// <summary>Clean exit: remove the marker (only if it is ours).</summary>
    public static void DisarmMarker(string dir, int pid)
    {
        try
        {
            string mp = System.IO.Path.Combine(dir, MarkerName); if (!File.Exists(mp)) return;
            foreach (var l in File.ReadAllLines(mp)) if (l == "pid=" + pid) { File.Delete(mp); return; }
        }
        catch (Exception) { }
    }
}
