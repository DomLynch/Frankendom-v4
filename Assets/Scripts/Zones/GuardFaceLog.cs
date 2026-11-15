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
        static string Dump(MonoBehaviour m)
        {
            var o = new System.Text.StringBuilder();
            foreach (var fi in m.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var t = fi.FieldType; if (!(t == typeof(bool) || t == typeof(int) || t == typeof(float) || t == typeof(string) || t.IsEnum || t == typeof(Vector3) || t == typeof(Vector2))) continue;
                object v; try { v = fi.GetValue(m); } catch (System.Exception) { continue; }
                if (v is float) v = ((float)v).ToString("F2"); else if (v is Vector3) v = ((Vector3)v).ToString("F1");
                o.Append(fi.Name).Append('=').Append(v).Append(' ');
            }
            return o.ToString().Trim();
        }
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
                if (at12) foreach (var m in f.GetComponents<MonoBehaviour>()) { if (m == null) continue; string tn = m.GetType().Name; if (tn == "ChaseAgent" || tn == "AIBrain" || tn == "GuardTorsoTurn") sb.Append("{" + tn + ": " + Dump(m) + "} "); }   // FK15 diagnostic (Lead): any field that holds a state, a hold/flee flag or a target position
                object fo = faceF != null ? faceF.GetValue(f) : null;
                Debug.Log("[GuardFace" + (at12 ? "+12" : "") + "] " + f.name + " opp=" + (f.opponent != null ? f.opponent.name : "none") + " faceOpp=" + (fo != null ? fo.ToString() : "?") + " state=" + f.state + " y=" + f.transform.eulerAngles.y.ToString("F1") + " pos=(" + f.transform.position.x.ToString("F2") + "," + f.transform.position.z.ToString("F2") + ") comps=[" + sb.ToString().Trim() + "]");
            }
        }
    }
}