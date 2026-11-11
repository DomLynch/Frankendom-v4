#if !FRANKEN_NO_HARNESS // DATAWATCH v1: harness / dev code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

// DATAWATCH v1 (laneDATA1): live data tuning without a restart. Polls the write time of the json files named in data_watcher.json (file-time poll: no FileSystemWatcher, no extra dependencies)
// and, when a file changed and has settled, calls the existing static Reload() of the system that owns it (type + method are DATA rows, resolved by reflection; nothing here knows a system by name).
// Dark: does nothing at all unless data_watcher.json says enabled:true or the launch arg -datawatch is present (absent file = nothing). It never writes a file and never touches combat or camera code:
// it only calls the same parameterless static Reload* methods the loaders already have. Only methods named Reload* are ever invoked. Files that are unsafe to reload live are listed under "skip" with the reason.
[Serializable] public class DataWatchCall { public string type = "", method = "Reload"; }
[Serializable] public class DataWatchRow { public string file = ""; public DataWatchCall[] calls = new DataWatchCall[0]; public string note = ""; }
[Serializable] public class DataWatchSkip { public string file = "", reason = ""; }
[Serializable] public class DataWatchCfg { public bool enabled = false, log = true, reloadOnFirstSight = false; public float pollSeconds = 0.5f, settleSeconds = 0.4f; public DataWatchRow[] rows = new DataWatchRow[0]; public DataWatchSkip[] skip = new DataWatchSkip[0]; }

public sealed class DataWatcher : MonoBehaviour
{
    const string SelfFile = "data_watcher.json";
    static DataWatcher inst;
    DataWatchCfg C; bool armedByArg; float next; long selfStamp;
    sealed class St { public long last, pending; float pendingSince; public void Seen(long s, float now) { if (s != pending) { pending = s; pendingSince = now; } } public float Age(float now) { return now - pendingSince; } }
    readonly Dictionary<string, St> st = new Dictionary<string, St>();
    readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return;
        bool arg = false; foreach (var a in Environment.GetCommandLineArgs()) if (string.Equals(a, "-datawatch", StringComparison.OrdinalIgnoreCase)) arg = true;
        var c = ReadCfg();
        if (!arg && (c == null || !c.enabled)) return;
        if (c == null) { Debug.LogWarning("[DataWatcher] -datawatch given but " + SelfFile + " is missing or unreadable: nothing to watch"); return; }
        var go = new GameObject("DataWatcher"); go.hideFlags = HideFlags.HideAndDontSave; DontDestroyOnLoad(go);
        inst = go.AddComponent<DataWatcher>(); inst.armedByArg = arg; inst.Configure(c, true);
    }

    static DataWatchCfg ReadCfg()
    {
        string t = DataPath.ReadText(SelfFile); if (string.IsNullOrEmpty(t)) return null;
        try { return JsonUtility.FromJson<DataWatchCfg>(t.TrimStart('﻿')); } catch (Exception e) { Debug.LogWarning("[DataWatcher] " + SelfFile + " unreadable, keeping the last config: " + e.Message); return null; }
    }

    // newest write time over every place the file can live (Assets/Data and StreamingAssets in the Editor, StreamingAssets and <exe>_Data/Data in a player)
    static long Stamp(string file)
    {
        long best = 0; foreach (var p in DataPath.Candidates(file)) { try { if (File.Exists(p)) { long t = File.GetLastWriteTimeUtc(p).Ticks; if (t > best) best = t; } } catch { } }
        return best;
    }

    void Configure(DataWatchCfg c, bool first)
    {
        C = c; if (C.rows == null) C.rows = new DataWatchRow[0]; if (C.skip == null) C.skip = new DataWatchSkip[0];
        var keep = new Dictionary<string, St>(st); st.Clear();
        foreach (var r in C.rows)
        {
            if (r == null || string.IsNullOrEmpty(r.file) || string.Equals(r.file, SelfFile, StringComparison.OrdinalIgnoreCase)) continue;
            St s; if (!keep.TryGetValue(r.file, out s)) { s = new St(); s.last = C.reloadOnFirstSight ? 0 : Stamp(r.file); s.pending = s.last; }
            st[r.file] = s;
        }
        selfStamp = Stamp(SelfFile);
        if (C.log) Debug.Log("[DataWatcher] " + (first ? "armed (" + (armedByArg ? "-datawatch" : SelfFile + " enabled") + ")" : "config reloaded") + ": watching " + st.Count + " file(s), poll " + C.pollSeconds.ToString("0.##") + " s, settle " + C.settleSeconds.ToString("0.##") + " s, " + C.skip.Length + " file(s) deliberately skipped (see the skip rows)");
    }

    void Update()
    {
        if (C == null) return; float now = Time.unscaledTime; if (now < next) return; next = now + Mathf.Max(0.1f, C.pollSeconds);
        long ss = Stamp(SelfFile);
        if (ss != selfStamp) { selfStamp = ss; var c = ReadCfg(); if (c != null) { Configure(c, false); if (!c.enabled && !armedByArg && c.log) Debug.Log("[DataWatcher] enabled:false: paused until it is switched on again"); } }
        if (!C.enabled && !armedByArg) return;
        foreach (var r in C.rows)
        {
            St s; if (r == null || string.IsNullOrEmpty(r.file) || !st.TryGetValue(r.file, out s)) continue;
            long cur = Stamp(r.file); if (cur == s.last) { s.pending = cur; continue; }
            s.Seen(cur, now); if (s.Age(now) < Mathf.Max(0f, C.settleSeconds)) continue;   // a file still being written: wait until two polls agree
            s.last = cur; Fire(r);
        }
    }

    void Fire(DataWatchRow r)
    {
        if (r.calls == null || r.calls.Length == 0) { if (C.log) Debug.Log("[DataWatcher] " + r.file + " changed, no owner call listed (nothing to do)"); return; }
        foreach (var k in r.calls)
        {
            if (k == null || string.IsNullOrEmpty(k.type)) continue; string m = string.IsNullOrEmpty(k.method) ? "Reload" : k.method;
            var mi = Resolve(k.type, m);
            if (mi == null) { Debug.LogWarning("[DataWatcher] " + r.file + " -> " + k.type + "." + m + "(): no such parameterless static Reload* method, skipped"); continue; }
            try { mi.Invoke(null, null); if (C.log) Debug.Log("[DataWatcher] reloaded " + r.file + " -> " + k.type + "." + m + "()"); }
            catch (Exception e) { var ie = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; Debug.LogWarning("[DataWatcher] " + r.file + " -> " + k.type + "." + m + "() threw " + ie.GetType().Name + ": " + ie.Message + " (the loader keeps its old values where it can)"); }
        }
    }

    MethodInfo Resolve(string type, string method)
    {
        string key = type + "." + method; MethodInfo mi; if (methods.TryGetValue(key, out mi)) return mi;
        mi = null;
        if (method.StartsWith("Reload", StringComparison.Ordinal))   // hard guard: this component can only ever call Reload*
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null; try { t = asm.GetType(type, false); } catch { }
                if (t == null) continue;
                mi = t.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null); if (mi != null) break;
            }
        methods[key] = mi; return mi;
    }
}
#endif
