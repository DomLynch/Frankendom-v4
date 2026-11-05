using System;
using System.Collections.Generic;
using UnityEngine;

// HCKIT4 v95 (HC3 K9): CueBus data. audio_manifest.json = the V3 sprite slice table (cue -> [start, duration, ...] seconds), the voice / gain / pitch numbers and the built-in event -> cue
// mapping (used only while cues.json is off). Everything was a literal in CueBus.cs. The slice table has NO code copy any more: a missing file = no cues (warning), like a missing sprite.
public static class AudioManifest
{
    [Serializable] public class CueRow { public string id = ""; public float[] slices = new float[0]; }
    [Serializable] public class FallbackRow { public string evt = "", cond = "*", cue = ""; public float gain = 1f, delay = 0f, rate = 1f; }
    [Serializable]
    public class Data
    {
        public float spriteSeconds = 46.108f, outputGain = 0.65f, pitchSpread = 0.05f, edgeFadeS = 0.008f, scheduleLeadS = 0.01f, minPitch = 0.1f;
        public int voices = 8, frameCap = 4, deathFrameCap = 8, seed = 731;
        public CueRow[] cues = new CueRow[0];
        public FallbackRow[] fallback = new FallbackRow[0];
    }

    static Data data;
    public static Data Get()
    {
        if (data != null) return data;
        var d = new Data();
        try { string s = DataPath.ReadText("audio_manifest.json"); if (!string.IsNullOrEmpty(s)) { var r = JsonUtility.FromJson<Data>(s); if (r != null) d = r; } else Debug.LogWarning("AudioManifest: audio_manifest.json not found; CueBus has no cues."); }
        catch (Exception ex) { Debug.LogWarning("AudioManifest: read failed: " + ex.Message); }
        if (d.cues == null) d.cues = new CueRow[0]; if (d.fallback == null) d.fallback = new FallbackRow[0];
        if (d.voices < 1) d.voices = 1; if (d.frameCap < 1) d.frameCap = 1; if (d.deathFrameCap < d.frameCap) d.deathFrameCap = d.frameCap;
        data = d; return d;
    }
    public static void Reload() { data = null; }

    /// <summary>cue id -> flat [start, duration, start, duration ...] seconds in the sprite</summary>
    public static Dictionary<string, float[]> Slices()
    {
        var m = new Dictionary<string, float[]>(); var d = Get();
        for (int i = 0; i < d.cues.Length; i++) { var c = d.cues[i]; if (c != null && !string.IsNullOrEmpty(c.id) && c.slices != null && c.slices.Length >= 2) m[c.id] = c.slices; }
        return m;
    }
}
