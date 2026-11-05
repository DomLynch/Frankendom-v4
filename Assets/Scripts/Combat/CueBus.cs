using System.Collections.Generic;
using UnityEngine;

// Audio cue layer ported from Frankendom V3 (src/audio/manifest.ts + src/fight/sound/cues.ts + audio-rules-defs.json):
// the 46.108 s CC0 sprite (Assets/Audio/sprite.ogg, SOURCES.json) is sliced once into per-variant clips, cues are picked by the same event map and gains,
// variants never repeat their last (nextVariant), +-5 % pitch spread, 8 voices, output gain .65, at most 4 cues per frame (8 on a death).
public class CueBus : MonoBehaviour
{
    public AudioClip sprite;
    static float SpriteSeconds { get { return AudioManifest.Get().spriteSeconds; } }   // HCKIT4 v95 K9: audio_manifest.json (was const 46.108 s, gain .65, pitch .05, 8 voices, seed 731)
    static float OutputGain { get { return AudioManifest.Get().outputGain; } }
    static float PitchSpread { get { return AudioManifest.Get().pitchSpread; } }
    static int Voices { get { return AudioManifest.Get().voices; } }
    static uint BaseSeed { get { return (uint)AudioManifest.Get().seed; } }

    // cue -> variants [start, duration] in seconds (V3 MANIFEST) now lives in audio_manifest.json (cues[]), read once in Awake   // HCKIT4 v95 K9
    static Dictionary<string, float[]> Manifest { get { return AudioManifest.Slices(); } }

    readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
    readonly Dictionary<string, int> last = new Dictionary<string, int>();
    AudioSource[] voices; double[] busyUntil;
    uint seed; int frameUsed, frameCap, frameStamp = -1;   // HCKIT4 v95 K9: seed / frameCap are set in Awake (AudioManifest reads a file: not allowed in a MonoBehaviour field initializer)
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
        seed = BaseSeed; frameCap = AudioManifest.Get().frameCap; voices = new AudioSource[Voices]; busyUntil = new double[Voices];   // HCKIT4 v95
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
                int fade = Mathf.Min(n / 4, Mathf.RoundToInt(AudioManifest.Get().edgeFadeS * hz));   // HCKIT4 v95
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
        if (frameStamp != Time.frameCount) { frameStamp = Time.frameCount; frameUsed = 0; frameCap = AudioManifest.Get().frameCap; }   // HCKIT4 v95
        if (frameUsed >= frameCap) return;   // V3: 4 cues per frame (8 on a death)
        frameUsed++;
        last.TryGetValue(name, out int li); if (!last.ContainsKey(name)) li = -1;
        int idx = NextVariant(v.Length, li); last[name] = idx;
        int vi = 0; double oldest = double.MaxValue; double now = AudioSettings.dspTime;
        for (int i = 0; i < Voices; i++) { if (busyUntil[i] <= now) { vi = i; oldest = -1; break; } if (busyUntil[i] < oldest) { oldest = busyUntil[i]; vi = i; } }
        var a = voices[vi]; a.Stop(); a.clip = v[idx]; a.volume = Mathf.Clamp01(gain * OutputGain * AudioMix.CueGain(name)); /* RCHYGIENE v100: audio_mix.json category of this cue (sfx / ambience / ui / music) x its slider */ a.pitch = rate * (1f + PitchSpread * (Rand() * 2f - 1f));
        double start = now + Mathf.Max(0f, delay) + AudioManifest.Get().scheduleLeadS; a.PlayScheduled(start); busyUntil[vi] = start + v[idx].length / Mathf.Max(AudioManifest.Get().minPitch, a.pitch);   // HCKIT4 v95
        played++;
        Log.Add($"{AutoShot.RecFrame / 30f + Mathf.Max(0f, delay):F4}\t{name}\t{idx}\t{a.volume:F4}\t{a.pitch:F4}");
    }

    static bool IsHeavy(MoveDef m) { return CombatClass.IsHeavyMove(m); }   // HCKIT4 v95 K8: combat_classes.json heavyMovePrefixes / heavyMoveIds
    // HCKIT4 v95 K9: the built-in event -> cue mapping (used only while cues.json is off) = audio_manifest.json fallback rows (evt + cond), played in file order
    void Fb(string evt, string cond)
    {
        var rows = AudioManifest.Get().fallback;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r != null && r.evt == evt && r.cond == cond) Play(r.cue, r.gain, r.delay, r.rate <= 0f ? 1f : r.rate); }
    }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        bool kick = a.CurrentMove != null && CombatClass.IsKick(a.CurrentMove.id);   // HCKIT4 v95
        {   // P2: cues.json (weapon x surface x weight); falls through to the built-in mapping below when the table is off
            var hr = type == "Hit" ? HitReactionTable.Last : null;
            string evt = type == "Hit" ? "hit" : type == "GuardBroken" ? "guard_break" : type == "Parried" ? "parry" : type == "Blocked" ? (perfect ? "block_perfect" : "block") : "";
            if (evt != "" && CueTable.Dispatch(this, evt, a, d, hr != null ? hr.weight : (kick ? "kick" : heavy ? "heavy" : "light"), a.CurrentMove != null ? a.CurrentMove.id : "")) return;
        }
        switch (type)   // HCKIT4 v95 K9
        {
            case CombatClass.Hit: Fb("hit", kick ? "kick" : heavy ? "heavy" : "light"); break;
            case CombatClass.GuardBroken: Fb("guard_break", "*"); break;
            case CombatClass.Parried: Fb("parry", "*"); break;
            case CombatClass.Blocked: Fb("block", perfect ? "perfect" : "normal"); break;
        }
    }
    void OnAttackStarted(DuelFighter f, MoveDef m)
    {
        if (CueTable.Dispatch(this, "attack_start", f, null, CombatClass.IsKick(m.id) ? "kick" : IsHeavy(m) ? "heavy" : "light", m.id)) return;   // P2: cues.json   // HCKIT4 v95
        Fb("attack_start", CombatClass.IsKick(m.id) ? "kick" : IsHeavy(m) ? "heavy" : "light");   // HCKIT4 v95 K9
    }
    void OnAttackMissed(DuelFighter f, MoveDef m) { if (CueTable.Dispatch(this, "attack_miss", f, null, m != null && CombatClass.IsKick(m.id) ? "kick" : IsHeavy(m) ? "heavy" : "light", m != null ? m.id : "")) return; Fb("attack_miss", "*"); }   // HCKIT4 v95
    void OnAction(DuelFighter f, string action) { if (CueTable.Dispatch(this, action, f, null, "light", "")) return; Fb(action, "*"); }   // HCKIT4 v95
    void OnKilled(DuelFighter f, MoveDef m)
    {
        frameStamp = Time.frameCount; frameCap = AudioManifest.Get().deathFrameCap; frameUsed = Mathf.Min(frameUsed, frameCap - 1);   // HCKIT4 v95
        var hr = HitReactionTable.Last;   // P2: the killing blow (set by Duel.Resolve before Die); victim = f
        if (CueTable.Dispatch(this, "kill", hr != null ? hr.attacker : null, f, hr != null ? hr.weight : "light", hr != null ? hr.moveId : (m != null ? m.id : ""))) return;
        Fb("kill", "*"); Fb("kill", CombatClass.IsStab(m) ? "stab" : "cut");   // HCKIT4 v95 K9
    }
}


