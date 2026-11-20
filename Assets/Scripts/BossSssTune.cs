using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// BOSSSSS TUNE v1 (fk_bosssss, 2026-11-20). COMPILE-ONLY, PRESENTATION ONLY. Static class: no MonoBehaviour, no serialized field, no RuntimeInitializeOnLoadMethod.
// Called from CharTwinDriver.LateUpdate (the boot path that already applies char_sss.json), at that driver's poll cadence.
// Data: boss_sss_tune.json (Data + StreamingAssets twins). enabled=false or the file absent = NOTHING is touched (no Resources.Load, no setter); if it was on, every value is put back to the shipped baseline.
//   profiles[] { profile (Resources path, e.g. BossSSS/Boss_Sobek_Hide), enabled, distanceMul (x scatteringDistance, 1 = shipped), tintMul (x transmissionTint rgb, 1 = shipped), worldScale (absolute, 0 = leave) }
//   twins[]    { twin (Resources path, same as char_sss.json rows), enabled, thickness (absolute _Thickness on the live twin instance, negative = leave) }
// Only public DiffusionProfileSettings setters are used (each calls UpdateCache). The profile asset is shared with the pipeline; the Editor is a no-op so a play session never dirties the .asset.
// Twin materials are per-run instances owned by CharTwinGroup (destroyed and re-created on a json reload), so the value is re-checked every poll and only written when it differs.
// Not touched here: Grendel and Pitborn (no row = untouched), albedo, colour grade, thicknessRemap (internal in HDRP), the SSS mask.
public static class BossSssTune
{
    [Serializable] public class ProfileRow { public string profile = ""; public bool enabled = true; public float distanceMul = 1f, tintMul = 1f, worldScale = 0f; }
    [Serializable] public class TwinRow { public string twin = ""; public bool enabled = true; public float thickness = -1f; }
    [Serializable] public class Cfg { public bool enabled; public bool log = true; public ProfileRow[] profiles = new ProfileRow[0]; public TwinRow[] twins = new TwinRow[0]; }

    class Base { public DiffusionProfileSettings asset; public Color distance, tint; public float worldScale; public bool changed; }

    const string FileName = "boss_sss_tune.json";
    static Cfg cfg; static DateTime stamp = DateTime.MinValue; static bool loadedOnce, dirty;
    static readonly Dictionary<string, Base> baseline = new Dictionary<string, Base>();
    static readonly Dictionary<string, float> thickBase = new Dictionary<string, float>();
    static readonly HashSet<string> warned = new HashSet<string>();
    static readonly int ThicknessId = Shader.PropertyToID("_Thickness");

    public static void Tick(List<CharTwinGroup> groups)
    {
#if !UNITY_EDITOR
        try
        {
            Reload();
            if (cfg == null || !cfg.enabled) { if (baseline.Count > 0 || thickBase.Count > 0) Restore(groups); return; }
            ApplyProfiles(); ApplyTwins(groups);
        }
        catch (Exception e) { if (warned.Add("tick:" + e.Message)) Debug.LogWarning("[BossSssTune] tick failed: " + e.Message); }
#endif
    }

    static void Reload()
    {
        string path = DataPath.Find(FileName);
        if (path == null) { if (cfg != null) dirty = true; cfg = null; loadedOnce = false; stamp = DateTime.MinValue; return; }
        DateTime written = System.IO.File.GetLastWriteTimeUtc(path);
        if (loadedOnce && written == stamp) return;
        loadedOnce = true; stamp = written; dirty = true;
        try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(path)); }
        catch (Exception e) { Debug.LogWarning("[BossSssTune] bad " + FileName + ": " + e.Message); cfg = null; }
    }

    static Base Capture(string path)
    {
        Base found; if (baseline.TryGetValue(path, out found)) return found;
        var asset = Resources.Load<DiffusionProfileSettings>(path);
        if (asset == null) { if (warned.Add(path)) Debug.LogWarning("[BossSssTune] profile not found in Resources: " + path); return null; }
        found = new Base { asset = asset, distance = asset.scatteringDistance, tint = asset.transmissionTint, worldScale = asset.worldScale };
        baseline[path] = found; return found;
    }

    static void ApplyProfiles()
    {
        if (!dirty || cfg.profiles == null) return;
        dirty = false;
        foreach (var row in cfg.profiles)
        {
            if (row == null || string.IsNullOrEmpty(row.profile)) continue;
            var snap = Capture(row.profile); if (snap == null || snap.asset == null) continue;
            bool want = row.enabled && (row.distanceMul != 1f || row.tintMul != 1f || row.worldScale > 0f);
            if (!want) { Put(row.profile, snap); continue; }   // identity row: shipped values untouched, no setter call unless something was changed before
            var dist = snap.distance; dist.r *= row.distanceMul; dist.g *= row.distanceMul; dist.b *= row.distanceMul;
            var tint = snap.tint; tint.r *= row.tintMul; tint.g *= row.tintMul; tint.b *= row.tintMul;
            snap.asset.scatteringDistance = dist; snap.asset.transmissionTint = tint;
            snap.asset.worldScale = row.worldScale > 0f ? row.worldScale : snap.worldScale; snap.changed = true;
            if (cfg.log) Debug.Log("[BossSssTune] profile " + row.profile + " distanceMul " + row.distanceMul + " tintMul " + row.tintMul + " worldScale " + snap.asset.worldScale);
        }
    }

    static void Put(string path, Base snap)
    {
        if (!snap.changed || snap.asset == null) return;
        snap.asset.scatteringDistance = snap.distance; snap.asset.transmissionTint = snap.tint; snap.asset.worldScale = snap.worldScale; snap.changed = false;
        Debug.Log("[BossSssTune] profile " + path + " restored to shipped values");
    }

    static void ApplyTwins(List<CharTwinGroup> groups)
    {
        if (cfg.twins == null) return;
        foreach (var row in cfg.twins)
        {
            if (row == null || string.IsNullOrEmpty(row.twin)) continue;
            float target = row.enabled ? row.thickness : -1f;
            foreach (var group in groups)
            {
                var live = group.TwinInstance(row.twin); if (live == null || !live.HasProperty(ThicknessId)) continue;
                if (!thickBase.ContainsKey(row.twin)) { var asset = Resources.Load<Material>(row.twin); if (asset != null) thickBase[row.twin] = asset.GetFloat(ThicknessId); }
                float want; if (target >= 0f) want = target; else if (!thickBase.TryGetValue(row.twin, out want)) continue;
                if (live.GetFloat(ThicknessId) != want) { live.SetFloat(ThicknessId, want); if (cfg.log && target >= 0f) Debug.Log("[BossSssTune] twin " + row.twin + " _Thickness " + want); }
            }
        }
    }

    static void Restore(List<CharTwinGroup> groups)
    {
        foreach (var pair in baseline) Put(pair.Key, pair.Value);
        foreach (var pair in thickBase) foreach (var group in groups) { var live = group.TwinInstance(pair.Key); if (live != null && live.HasProperty(ThicknessId) && live.GetFloat(ThicknessId) != pair.Value) live.SetFloat(ThicknessId, pair.Value); }
        thickBase.Clear(); baseline.Clear(); dirty = true;
    }
}
