using System;
using System.IO;
using UnityEngine;

// MUSIC v1 (Dom's direct order, 18 Nov 2026): the background music, one looping track in every state for now. Data: music.json (tracks[], states{}, startDelayS, skipArgs ...); files: StreamingAssets/music/<file>.
// HOOK: MusicPlayer.Boot() is called from the EXISTING LoadingCard.Boot (an AfterSceneLoad hook already in the player's startup table). This file adds NO RuntimeInitializeOnLoadMethod and NO serialized field.
// STREAMING, not decoded: the WAV is read from disk in small blocks by an AudioClip stream callback (no UnityWebRequest, no module added to the manifest), so memory is a 64 KB file buffer, not 38 MB of PCM.
//   The loop wraps INSIDE the reader (back to the first sample of the data chunk), and a short fade (loopDeclickMs) at the file end / start removes a click at the join.
// Volume = track.volume * fade-in * the "music" category of audio_mix.json (AudioMix.Bind: the Music slider, master and the pause fade all apply). AudioListener.pause (pause menu) freezes it.
// Off in harness runs: -nomusic, -camscene, -qa (music.json skipArgs), RunMode.IsHarness, MenuCfg.Harness. "-music" forces it on in a harness run (to hear it).
// Log lines: [Music] armed / loaded / playing / looping / restarted / off.
public static class MusicPlayer
{
    [Serializable] public class Track { public string id = "", file = "", label = ""; public float volume = 0.4f, fadeInS = 4f; public bool loop = true; }
    [Serializable] public class States { public string any = "", menu = "", camp = "", explore = "", combat = "", boss = "", death = "", end = ""; }   // v1 plays only "any" (or tracks[0]); the rest are placeholders for later
    [Serializable] public class Cfg
    {
        public bool enabled = true; public string category = "music", forceArg = "-music"; public float startDelayS = 0.5f, loopDeclickMs = 30f;
        public string[] skipArgs = new[] { "-nomusic", "-camscene", "-qa" }; public Track[] tracks = new Track[0]; public States states = new States();
    }

    static Cfg cfg; static MusicDriver driver;
    public static bool Playing { get { return driver != null && driver.IsPlaying; } }

    /// <summary>Called once from LoadingCard.Boot (existing boot path). Safe to call twice.</summary>
    public static void Boot()
    {
        if (driver != null) return;
        try
        {
            string t = DataPath.ReadText("music.json"); if (string.IsNullOrEmpty(t)) { Debug.Log("[Music] off: file missing/failed: no music.json"); return; }
            cfg = JsonUtility.FromJson<Cfg>(t.TrimStart((char)0xFEFF)); if (cfg == null || !cfg.enabled || cfg.tracks == null || cfg.tracks.Length == 0) { Debug.Log("[Music] off: music.json disabled (enabled=false) or parse failed or no tracks"); return; }
            var args = Environment.GetCommandLineArgs(); bool force = Array.IndexOf(args, cfg.forceArg) >= 0;
            if (!force)
            {
                // v2: prefix match like LoadingCard ("-camscene:x" / "-camscene=x" / "-qa..." are the same flag); every off path names its reason
                foreach (var a in args) foreach (var s in cfg.skipArgs) if (!string.IsNullOrEmpty(s) && a.StartsWith(s, StringComparison.OrdinalIgnoreCase)) { Debug.Log("[Music] off: " + s + " (QA/harness flag on the command line: " + a + ")"); return; }
                if (Application.isBatchMode) { Debug.Log("[Music] off: batchmode (harness run)"); return; }
                if (RunMode.IsHarness) { Debug.Log("[Music] off: harness run (RunMode.IsHarness: a flag of run_mode.json is on the command line)"); return; }
                if (MenuCfg.Harness) { Debug.Log("[Music] off: harness run (MenuCfg.Harness: a flag of menu.json is on the command line)"); return; }
            }
            var go = new GameObject("MusicPlayer"); go.hideFlags = HideFlags.HideInHierarchy; UnityEngine.Object.DontDestroyOnLoad(go);
            driver = go.AddComponent<MusicDriver>(); driver.Init(cfg);
            Debug.Log("[Music] armed (" + (force ? "forced by " + cfg.forceArg : "normal run, no QA/harness flag") + "): starts after the loading card is gone (+" + cfg.startDelayS.ToString("F1") + " s)");
        }
        catch (Exception e) { Debug.LogWarning("[Music] off: file missing/failed: " + e.GetType().Name + " " + e.Message); }
    }

    static Track Pick(Cfg c)
    {
        string id = c.states != null ? c.states.any : "";
        if (!string.IsNullOrEmpty(id)) foreach (var t in c.tracks) if (t != null && t.id == id) return t;
        foreach (var t in c.tracks) if (t != null && !string.IsNullOrEmpty(t.file)) return t;
        return null;
    }

    static string FindFile(string file)
    {
        string sa = Path.Combine(Application.streamingAssetsPath, "music/" + file), dt = Path.Combine(Application.dataPath, "Data/music/" + file);
#if UNITY_EDITOR
        return File.Exists(dt) ? dt : (File.Exists(sa) ? sa : null);
#else
        return File.Exists(sa) ? sa : (File.Exists(dt) ? dt : null);
#endif
    }

    /// <summary>Hidden carrier: waits for the loading card, opens the stream, fades in, keeps it playing. DontDestroyOnLoad, so scene reloads / Retry never touch it.</summary>
    class MusicDriver : MonoBehaviour
    {
        Cfg c; Track tr; AudioSource src; WavStream ws; int frames; float t0 = -1f, fadeT; bool started, failed, looped;
        float lastRestart = -99f;
        public bool IsPlaying { get { return started && src != null && src.isPlaying; } }
        public void Init(Cfg cfg) { c = cfg; }

        void Update()
        {
            if (failed) return;
            frames++;
            if (!started)
            {
                if (frames < 3 || !LoadingCard.Cleared) return;
                if (t0 < 0f) t0 = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - t0 < c.startDelayS) return;
                Begin(); return;
            }
            if (src == null) return;
            if (fadeT < tr.fadeInS) { fadeT += Time.unscaledDeltaTime; float k = tr.fadeInS <= 0f ? 1f : Mathf.Clamp01(fadeT / tr.fadeInS); k = k * k * (3f - 2f * k); AudioMix.Bind(src, c.category, tr.volume * k); }
            else if (fadeT < 1e8f) { fadeT = 1e9f; AudioMix.Bind(src, c.category, tr.volume); }
            // never stops: a stopped source (device change, voice limit) restarts, except while the pause menu holds the listener
            if (!src.isPlaying && !AudioListener.pause && !AudioMix.Paused && Time.realtimeSinceStartup - lastRestart > 2f) { lastRestart = Time.realtimeSinceStartup; src.Play(); Debug.Log("[Music] restarted (source had stopped)"); }
            if (!looped && ws != null && ws.Wraps > 0) { looped = true; Debug.Log("[Music] looping: reached the end of '" + tr.file + "' and wrapped to the start without a gap"); }
        }

        void Begin()
        {
            tr = Pick(c); if (tr == null) { failed = true; Debug.LogWarning("[Music] off: file missing/failed: no usable track in music.json"); return; }
            string path = FindFile(tr.file); if (path == null) { failed = true; Debug.LogWarning("[Music] off: file missing/failed: not found music/" + tr.file + " (looked in StreamingAssets and Data)"); return; }
            float t = Time.realtimeSinceStartup;
            try { ws = new WavStream(path, c.loopDeclickMs, tr.loop); }
            catch (Exception e) { failed = true; Debug.LogWarning("[Music] off: file missing/failed: cannot open " + path + " (" + e.Message + ")"); return; }
            try {
            var clip = AudioClip.Create("music_" + tr.id, ws.Frames, ws.Channels, ws.Rate, true, ws.Read, ws.SetPos);
            src = gameObject.AddComponent<AudioSource>(); src.clip = clip; src.loop = tr.loop; src.playOnAwake = false; src.spatialBlend = 0f; src.priority = 0; src.volume = 0f; src.bypassEffects = true; src.bypassReverbZones = true;
            AudioMix.Bind(src, c.category, 0f); src.Play(); started = true; fadeT = 0f;
            } catch (Exception e) { failed = true; started = false; Debug.LogWarning("[Music] off: file missing/failed: audio source / clip creation failed (" + e.GetType().Name + " " + e.Message + ")"); return; }
            Debug.Log("[Music] loaded " + tr.file + " (" + ws.Channels + " ch, " + ws.Rate + " Hz, " + ws.Bits + " bit, " + (ws.Frames / (float)ws.Rate).ToString("F1") + " s, streamed from disk, header read in " + ((Time.realtimeSinceStartup - t) * 1000f).ToString("F1") + " ms)");
            Debug.Log("[Music] playing '" + tr.id + "' base volume " + tr.volume.ToString("F2") + " x music slider " + AudioMix.Gain(c.category).ToString("F2") + ", fade in " + tr.fadeInS.ToString("F1") + " s, loop " + tr.loop);
        }

        void OnDestroy() { if (ws != null) ws.Close(); }
    }

    /// <summary>PCM WAV (16 bit or 32 bit float) read in blocks on the audio thread. Wraps to the first sample at the end of the data chunk; fades the first / last loopDeclickMs of the file.</summary>
    class WavStream
    {
        FileStream fs; readonly object gate = new object(); long dataStart, dataLen, pos; int bps, blockAlign, fmtTag, declick; bool loop, justWrapped; byte[] buf = new byte[0];
        public int Channels, Rate, Bits; public int Frames; public volatile int Wraps;

        public WavStream(string path, float declickMs, bool looping)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536); loop = looping;
            var h = new byte[12]; if (fs.Read(h, 0, 12) < 12 || h[0] != 'R' || h[1] != 'I' || h[2] != 'F' || h[3] != 'F' || h[8] != 'W' || h[9] != 'A' || h[10] != 'V' || h[11] != 'E') throw new IOException("not a RIFF WAVE file");
            bool gotFmt = false, gotData = false; var ch = new byte[8];
            while (!gotData && fs.Read(ch, 0, 8) == 8)
            {
                long size = BitConverter.ToUInt32(ch, 4); string id = "" + (char)ch[0] + (char)ch[1] + (char)ch[2] + (char)ch[3]; long body = fs.Position;
                if (id == "fmt ")
                {
                    var f = new byte[Math.Min(size, 40)]; fs.Read(f, 0, f.Length); fmtTag = BitConverter.ToUInt16(f, 0); Channels = BitConverter.ToUInt16(f, 2); Rate = (int)BitConverter.ToUInt32(f, 4); blockAlign = BitConverter.ToUInt16(f, 12); Bits = BitConverter.ToUInt16(f, 14);
                    if (fmtTag == 0xFFFE && f.Length >= 26) fmtTag = BitConverter.ToUInt16(f, 24);
                    gotFmt = true;
                }
                else if (id == "data") { if (!gotFmt) throw new IOException("data before fmt"); dataStart = body; dataLen = Math.Min(size, fs.Length - body); gotData = true; break; }
                fs.Position = body + size + (size & 1);
            }
            if (!gotFmt || !gotData) throw new IOException("fmt/data chunk missing");
            bps = Bits / 8; if (!((fmtTag == 1 && Bits == 16) || (fmtTag == 3 && Bits == 32))) throw new IOException("unsupported WAV format (tag " + fmtTag + ", " + Bits + " bit): use 16 bit PCM or 32 bit float");
            if (Channels < 1 || Channels > 2 || blockAlign != bps * Channels) throw new IOException("unsupported channel layout");
            dataLen -= dataLen % blockAlign; Frames = (int)(dataLen / blockAlign); declick = Mathf.Max(0, Mathf.RoundToInt(declickMs * 0.001f * Rate)); fs.Position = dataStart; pos = 0;
        }

        public void Close() { lock (gate) { if (fs != null) { fs.Dispose(); fs = null; } } }

        // Unity rewinds a looping stream clip with SetPos(0) when ITS read position reaches the end; the reader has already wrapped inside Read, so that one rewind is skipped (else the first few ms would play twice)
        public void SetPos(int frame) { lock (gate) { if (fs == null) return; if (frame == 0 && justWrapped) { justWrapped = false; return; } justWrapped = false; pos = Math.Max(0, Math.Min(dataLen, (long)frame * blockAlign)); fs.Position = dataStart + pos; } }

        public void Read(float[] data)
        {
            lock (gate)
            {
                int need = data.Length * bps, got = 0, zeroReads = 0; if (buf.Length < need) buf = new byte[need];
                try
                {
                    while (got < need && fs != null)
                    {
                        long left = dataLen - pos;
                        if (left <= 0) { if (!loop) break; fs.Position = dataStart; pos = 0; Wraps++; justWrapped = true; left = dataLen; }
                        int n = (int)Math.Min(need - got, left); int r = fs.Read(buf, got, n);
                        if (r <= 0) { if (++zeroReads > 2) break; fs.Position = dataStart; pos = 0; continue; }
                        Convert(data, got / bps, r / bps, buf, got, pos / bps); pos += r; got += r;
                    }
                }
                catch (Exception) { }
                for (int i = got / bps; i < data.Length; i++) data[i] = 0f;
            }
        }

        // count samples go to data[sample0..]; byteOff = where they sit in the byte buffer; firstSample = index of the first of them in the file (interleaved), for the loop-join fade
        void Convert(float[] data, int sample0, int count, byte[] b, int byteOff, long firstSample)
        {
            long totalSamples = (long)Frames * Channels; long dc = (long)declick * Channels;
            for (int i = 0; i < count; i++)
            {
                float v = bps == 2 ? (short)(b[byteOff + 2 * i] | (b[byteOff + 2 * i + 1] << 8)) / 32768f : BitConverter.ToSingle(b, byteOff + 4 * i);
                if (dc > 0) { long s = firstSample + i; if (s < dc) v *= (float)s / dc; else if (s >= totalSamples - dc) v *= (float)(totalSamples - s) / dc; }
                data[sample0 + i] = v;
            }
        }
    }
}
