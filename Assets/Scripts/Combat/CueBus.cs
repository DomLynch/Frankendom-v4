using System.Collections.Generic;
using UnityEngine;

// Audio cue layer ported from Frankendom V3 (src/audio/manifest.ts + src/fight/sound/cues.ts + audio-rules-defs.json):
// the 46.108 s CC0 sprite (Assets/Audio/sprite.ogg, SOURCES.json) is sliced once into per-variant clips, cues are picked by the same event map and gains,
// variants never repeat their last (nextVariant), +-5 % pitch spread, 8 voices, output gain .65, at most 4 cues per frame (8 on a death).
public class CueBus : MonoBehaviour
{
    public AudioClip sprite;
    const float SpriteSeconds = 46.108f, OutputGain = 0.65f, PitchSpread = 0.05f;
    const int Voices = 8, BaseSeed = 731;

    // cue -> variants [start, duration] in seconds (V3 MANIFEST)
    static readonly Dictionary<string, float[]> Manifest = new Dictionary<string, float[]>
    {
        { "whoosh_light", new[] { 0.02f, 0.192f, 0.272f, 0.185f, 0.517f, 0.182f, 0.759f, 0.181f } },
        { "whoosh_heavy", new[] { 1f, 0.291f, 1.352f, 0.294f, 1.706f, 0.253f, 2.019f, 0.272f } },
        { "draw", new[] { 2.351f, 0.3f, 2.711f, 0.3f } },
        { "hit_flesh", new[] { 3.071f, 0.62f, 3.751f, 0.45f, 4.261f, 0.75f, 5.071f, 0.6f, 5.731f, 0.7f } },
        { "hit_heavy", new[] { 6.491f, 0.75f, 7.301f, 0.55f, 7.911f, 0.852f, 8.824f, 0.682f, 9.565f, 0.795f } },
        { "hit_kick", new[] { 10.421f, 0.28f, 10.761f, 0.28f, 11.101f, 0.28f, 11.441f, 0.28f } },
        { "block", new[] { 11.781f, 0.5f, 12.341f, 0.5f, 12.901f, 0.48f, 13.441f, 0.48f } },
        { "block_perfect", new[] { 13.981f, 0.6f, 14.641f, 0.6f, 15.301f, 0.6f, 15.961f, 0.6f } },
        { "parry", new[] { 16.621f, 0.72f, 17.401f, 0.72f, 18.181f, 0.72f, 18.961f, 0.66f, 19.681f, 0.66f, 20.401f, 0.66f } },
        { "guard_break", new[] { 21.121f, 0.46f, 21.641f, 0.46f, 22.161f, 0.46f, 22.681f, 0.46f } },
        { "whip", new[] { 23.201f, 0.28f, 23.541f, 0.28f } },
        { "whip_raise", new[] { 23.881f, 0.4f, 24.341f, 0.4f } },
        { "charge", new[] { 24.801f, 0.5f, 25.361f, 0.5f } },
        { "kill", new[] { 25.921f, 0.62f, 26.601f, 0.82f, 27.481f, 0.7f } },
        { "roll", new[] { 28.241f, 0.32f, 28.621f, 0.32f, 29.001f, 0.32f, 29.381f, 0.32f } },
        { "backstep", new[] { 29.761f, 0.15f, 29.971f, 0.15f, 30.181f, 0.15f, 30.391f, 0.15f } },
        { "death_voice", new[] { 30.601f, 0.44f, 31.101f, 0.62f, 31.781f, 0.44f, 32.281f, 0.62f } },
        { "flesh_cut", new[] { 32.961f, 0.18f, 33.201f, 0.18f, 33.441f, 0.18f, 33.681f, 0.18f } },
        { "flesh_stab", new[] { 33.921f, 0.16f, 34.141f, 0.16f } },
        { "flesh_tear", new[] { 34.361f, 0.34f, 34.761f, 0.34f } },
        { "bone_crack", new[] { 35.161f, 0.5f, 35.721f, 0.38f } },
        { "crowd_gasp", new[] { 36.161f, 0.42f, 36.641f, 0.42f } },
        { "crowd_cheer", new[] { 37.121f, 2.5f, 39.681f, 2.5f, 42.241f, 2.5f } },
        { "charge_foe", new[] { 44.801f, 0.397f, 45.257f, 0.391f } },
        { "effort_voice", new[] { 45.708f, 0.15f, 45.918f, 0.15f } },
    };

    readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
    readonly Dictionary<string, int> last = new Dictionary<string, int>();
    AudioSource[] voices; double[] busyUntil;
    uint seed = BaseSeed; int frameUsed, frameCap = 4, frameStamp = -1;
    public int played;   // test hook (AutoShot report)
    public static readonly List<string> Log = new List<string>();   // -record: every cue with its game time, to mix the soundtrack offline

    // V3 seeded(): mulberry32
    float Rand() { unchecked { seed += 0x6d2b79f5u; uint t = (seed ^ (seed >> 15)) * (1u | seed); t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t; return (t ^ (t >> 14)) / 4294967296f; } }
    int NextVariant(int count, int lastIdx)
    {
        if (count < 2) return 0;
        if (lastIdx < 0 || lastIdx >= count) return Mathf.FloorToInt(Rand() * count);
        int pick = Mathf.FloorToInt(Rand() * (count - 1));
        return pick >= lastIdx ? pick + 1 : pick;
    }

    void Awake()
    {
        voices = new AudioSource[Voices]; busyUntil = new double[Voices];
        for (int i = 0; i < Voices; i++) { var go = new GameObject("Voice" + i); go.transform.SetParent(transform); var a = go.AddComponent<AudioSource>(); a.playOnAwake = false; a.spatialBlend = 0f; voices[i] = a; }
        if (!sprite) { Debug.LogWarning("CueBus: no sprite assigned"); return; }
        int ch = sprite.channels, hz = sprite.frequency; var data = new float[sprite.samples * ch];
        if (!sprite.GetData(data, 0)) { Debug.LogWarning("CueBus: sprite not readable"); return; }
        foreach (var kv in Manifest)
        {
            var v = new AudioClip[kv.Value.Length / 2];
            for (int i = 0; i < v.Length; i++)
            {
                int s0 = Mathf.Clamp(Mathf.RoundToInt(kv.Value[i * 2] * hz), 0, sprite.samples - 1), n = Mathf.Min(Mathf.RoundToInt(kv.Value[i * 2 + 1] * hz), sprite.samples - s0);
                var seg = new float[n * ch]; System.Array.Copy(data, s0 * ch, seg, 0, n * ch);
                // 8 ms edge fades so the slices never click
                int fade = Mathf.Min(n / 4, Mathf.RoundToInt(0.008f * hz));
                for (int k = 0; k < fade; k++) { float g = (float)k / fade; for (int c = 0; c < ch; c++) { seg[k * ch + c] *= g; seg[(n - 1 - k) * ch + c] *= g; } }
                var clip = AudioClip.Create(kv.Key + "_" + i, n, ch, hz, false); clip.SetData(seg, 0); v[i] = clip;
            }
            clips[kv.Key] = v;
        }
        Debug.Log($"CueBus: sprite {sprite.name} {sprite.length:F2}s ch={ch} hz={hz} cues={clips.Count}");
    }

    void OnEnable()
    {
        ContactRelay.OnContact += OnContact; ContactRelay.OnAttackStarted += OnAttackStarted; ContactRelay.OnAttackMissed += OnAttackMissed; ContactRelay.OnAction += OnAction; ContactRelay.OnKilled += OnKilled;
    }
    void OnDisable()
    {
        ContactRelay.OnContact -= OnContact; ContactRelay.OnAttackStarted -= OnAttackStarted; ContactRelay.OnAttackMissed -= OnAttackMissed; ContactRelay.OnAction -= OnAction; ContactRelay.OnKilled -= OnKilled;
    }

    public void Play(string name, float gain, float delay = 0f, float rate = 1f)
    {
        if (!clips.TryGetValue(name, out var v) || v.Length == 0) return;
        if (frameStamp != Time.frameCount) { frameStamp = Time.frameCount; frameUsed = 0; frameCap = 4; }
        if (frameUsed >= frameCap) return;   // V3: 4 cues per frame (8 on a death)
        frameUsed++;
        last.TryGetValue(name, out int li); if (!last.ContainsKey(name)) li = -1;
        int idx = NextVariant(v.Length, li); last[name] = idx;
        int vi = 0; double oldest = double.MaxValue; double now = AudioSettings.dspTime;
        for (int i = 0; i < Voices; i++) { if (busyUntil[i] <= now) { vi = i; oldest = -1; break; } if (busyUntil[i] < oldest) { oldest = busyUntil[i]; vi = i; } }
        var a = voices[vi]; a.Stop(); a.clip = v[idx]; a.volume = Mathf.Clamp01(gain * OutputGain); a.pitch = rate * (1f + PitchSpread * (Rand() * 2f - 1f));
        double start = now + Mathf.Max(0f, delay) + 0.01; a.PlayScheduled(start); busyUntil[vi] = start + v[idx].length / Mathf.Max(0.1f, a.pitch);
        played++;
        Log.Add($"{AutoShot.RecFrame / 30f + Mathf.Max(0f, delay):F4}\t{name}\t{idx}\t{a.volume:F4}\t{a.pitch:F4}");
    }

    static bool IsHeavy(MoveDef m) { if (m == null) return false; string id = m.id; return id.StartsWith("heavy") || id == "riposte" || id == "slash_riposte" || id.StartsWith("skill_"); }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        bool kick = a.CurrentMove != null && a.CurrentMove.id == "kick";
        switch (type)
        {
            case "Hit": Play(kick ? "hit_kick" : heavy ? "hit_heavy" : "hit_flesh", 0.3f); break;
            case "GuardBroken": Play("guard_break", 1f); Play("hit_flesh", 0.55f); break;
            case "Parried": Play("parry", 1f); break;
            case "Blocked": if (perfect) Play("block_perfect", 0.55f); else Play("block", 1f); break;
        }
    }
    void OnAttackStarted(DuelFighter f, MoveDef m)
    {
        if (m.id == "kick") Play("whoosh_light", 0.09f);
        else if (IsHeavy(m)) Play("whoosh_heavy", 0.18f);
        else Play("whoosh_light", 0.12f);
    }
    void OnAttackMissed(DuelFighter f, MoveDef m) { Play("whoosh_light", 0.1f, 0f, 0.8f); }
    void OnAction(DuelFighter f, string action) { if (action == "roll") Play("roll", 0.12f); else if (action == "backstep") Play("backstep", 0.09f); }
    void OnKilled(DuelFighter f, MoveDef m)
    {
        frameStamp = Time.frameCount; frameCap = 8; frameUsed = Mathf.Min(frameUsed, 7);
        Play("death_voice", 0.45f, 0.03f); Play("kill", 0.65f, 0.65f);
        bool stab = m != null && (m.id.Contains("thrust") || m.id == "riposte");
        Play(stab ? "flesh_stab" : "flesh_cut", 0.45f);
    }
}


