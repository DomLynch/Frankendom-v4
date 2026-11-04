using System;
using UnityEngine;

// Hit-stop on a presentation clock (V3 main.ts 605-608 / 667-668, hit-stop.ts stopFor). Time.timeScale is never touched.
//  RequestStop(ms): the sim accumulator (Duel.Update) stops accumulating and fighter Animators freeze (Fighter.ApplyFreeze) while Frozen;
//  particles, blood, the camera shove, the lock blend and Flinch keep running on their own (unscaled) time.
//  Tuning lives in Assets/Data/hit_stop.json (frames per contact tier / per move); only the hero special lands a 6-frame stop today.
[Serializable] public class HitStopRow { public string id; public int frames; public float ms; }
[Serializable]
public class HitStopTable
{
    public int tickHz = 60;
    public string kickMoveId = "kick", specialMovePrefix = "skill_";
    public string[] specialEventTypes = new string[0];
    public HitStopRow[] tiers = new HitStopRow[0];
    public HitStopRow[] moves = new HitStopRow[0];

    float MsOf(HitStopRow r) { return r == null ? 0f : r.ms + (tickHz > 0 ? r.frames * 1000f / tickHz : 0f); }
    static HitStopRow Find(HitStopRow[] rows, string id) { if (rows == null) return null; for (int i = 0; i < rows.Length; i++) if (rows[i] != null && rows[i].id == id) return rows[i]; return null; }
    bool IsSpecialType(string type) { if (specialEventTypes == null) return false; for (int i = 0; i < specialEventTypes.Length; i++) if (specialEventTypes[i] == type) return true; return false; }

    // stopFor(event): a per-move row wins, then the tier (kick, special, then by contact type and weight)
    public float StopMs(CombatEvent e)
    {
        string mid = e.moveId ?? "";
        var mr = Find(moves, mid); if (mr != null) return MsOf(mr);
        string tier = null;
        if (mid.Length > 0 && mid == kickMoveId) tier = "kick";
        else if (mid.Length > 0 && !string.IsNullOrEmpty(specialMovePrefix) && mid.StartsWith(specialMovePrefix) && IsSpecialType(e.type)) tier = "special";
        else switch (e.type)
        {
            case "Hit": tier = e.heavy ? "hitHeavy" : "hitLight"; break;
            case "Blocked": tier = e.heavy ? "blockHeavy" : "blockLight"; break;
            case "Parried": tier = "parry"; break;
            case "GuardBroken": tier = "guardBreak"; break;
        }
        return tier == null ? 0f : MsOf(Find(tiers, tier));
    }
}

public static class PresentationClock
{
    public static HitStopTable Table { get; private set; }
    static float remaining;      // seconds of stop left (unscaled)
    static float simFraction = 1f;
    public static bool Frozen { get { return remaining > 0f; } }
    public static float Remaining { get { return remaining; } }
    public static event Action<bool> OnFrozenChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { remaining = 0f; simFraction = 1f; Table = null; OnFrozenChanged = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Table == null) Table = Load();
        if (UnityEngine.Object.FindFirstObjectByType<PresentationClockDriver>() != null) return;
        var go = new GameObject("PresentationClock");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<PresentationClockDriver>();
    }

    static HitStopTable Load()
    {
        string[] paths = { System.IO.Path.Combine(Application.dataPath, "Data/hit_stop.json"), System.IO.Path.Combine(Application.streamingAssetsPath, "hit_stop.json") };
        foreach (var p in paths)
        {
            try { if (System.IO.File.Exists(p)) { var t = JsonUtility.FromJson<HitStopTable>(System.IO.File.ReadAllText(p)); if (t != null) return t; } }
            catch (Exception ex) { Debug.LogWarning("PresentationClock: could not read " + p + ": " + ex.Message); }
        }
        Debug.LogWarning("PresentationClock: hit_stop.json not found (Assets/Data or StreamingAssets); hit-stop disabled.");
        return new HitStopTable();
    }

    // longest stop wins (V3: stopFor takes the max); a request while frozen extends, never shortens
    public static void RequestStop(float ms)
    {
        if (ms <= 0f) return;
        bool was = Frozen;
        remaining = Mathf.Max(remaining, ms / 1000f);
        if (!was && OnFrozenChanged != null) OnFrozenChanged(true);
    }

    public static void RequestStop(CombatEvent e) { if (Table != null) RequestStop(Table.StopMs(e)); }

    // call once per rendered frame, before Duel.Update (the driver runs at execution order -1000)
    public static void Tick(float unscaledDt)
    {
        simFraction = 1f;
        if (remaining <= 0f) return;
        float spent = Mathf.Min(remaining, unscaledDt);
        remaining -= spent;
        if (remaining <= 1e-6f)
        {
            remaining = 0f;
            simFraction = unscaledDt > 0f ? Mathf.Clamp01((unscaledDt - spent) / unscaledDt) : 1f;   // V3: the unspent part of the frame still feeds the sim
            if (OnFrozenChanged != null) OnFrozenChanged(false);
        }
        else simFraction = 0f;
    }

    // sim delta for this frame (Duel.Update): 0 while frozen
    public static float SimDelta(float dt) { return Frozen ? 0f : dt * simFraction; }

    public static void Cancel() { if (Frozen) { remaining = 0f; if (OnFrozenChanged != null) OnFrozenChanged(false); } }
}

[DefaultExecutionOrder(-1000)]
public class PresentationClockDriver : MonoBehaviour
{
    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; PresentationClock.Cancel(); }
    void OnEvent(CombatEvent e) { PresentationClock.RequestStop(e); }
    void Update() { PresentationClock.Tick(Time.unscaledDeltaTime); }
}
