using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Lead/COO diagnostic (debug only, off unless the command line has -guardfacelog): one log line per zone creature ("#bandit_" spawns) 3 s after it first exists:
// opponent name, the private faceOppV flag, FState, transform.eulerAngles.y, and every MonoBehaviour on it with its enabled flag (brain / patrol state).
// Purpose: find why the z2 pickets show their backs to Joan whatever the spot yaw (DuelFighter faceOpp toward df.opponent, brain disabled, etc.).
public static class GuardFaceLog
{
    static bool on, started; static readonly HashSet<DuelFighter> seen = new HashSet<DuelFighter>(), seen12 = new HashSet<DuelFighter>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { started = false; on = false; seen.Clear(); seen12.Clear(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (started) return; started = true;
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-guardfacelog") on = true;
        if (!on) return;
        var go = new GameObject("GuardFaceLog"); go.hideFlags = HideFlags.HideInHierarchy; Object.DontDestroyOnLoad(go); go.AddComponent<Ticker>();
    }
    sealed class Ticker : MonoBehaviour
    {
        readonly Dictionary<DuelFighter, float> first = new Dictionary<DuelFighter, float>(); float next; static FieldInfo faceF;
        void Update()
        {
            if (Time.unscaledTime < next) return; next = Time.unscaledTime + 0.4f;
            if (faceF == null) faceF = typeof(DuelFighter).GetField("faceOppV", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
            {
                if (f == null || f.IsPlayer || f.name.IndexOf("#bandit_", System.StringComparison.Ordinal) < 0) continue;
                float t0; if (!first.TryGetValue(f, out t0)) { first[f] = Time.unscaledTime; continue; }
                bool at12 = seen.Contains(f);   // FK15: second line at +12 s (the guards after the restart settled)
                if (at12 ? (seen12.Contains(f) || Time.unscaledTime - t0 < 12f) : Time.unscaledTime - t0 < 3f) continue;
                if (at12) seen12.Add(f); else seen.Add(f);
                var sb = new System.Text.StringBuilder();
                foreach (var m in f.GetComponents<MonoBehaviour>()) { if (m == null) continue; sb.Append(m.GetType().Name).Append(m.enabled ? ":on " : ":off "); }
                object fo = faceF != null ? faceF.GetValue(f) : null;
                Debug.Log("[GuardFace" + (at12 ? "+12" : "") + "] " + f.name + " opp=" + (f.opponent != null ? f.opponent.name : "none") + " faceOpp=" + (fo != null ? fo.ToString() : "?") + " state=" + f.state + " y=" + f.transform.eulerAngles.y.ToString("F1") + " pos=(" + f.transform.position.x.ToString("F2") + "," + f.transform.position.z.ToString("F2") + ") comps=[" + sb.ToString().Trim() + "]");
            }
        }
    }
}