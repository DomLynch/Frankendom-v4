using System.Collections.Generic;
using System.Text;
using UnityEngine;

// DiagRendererBounds (unity-dev2, QA only, flag -boundsdiag[:armAfterSeconds]): names the renderer behind the one-frame "pale wedge + dark shards" glitch at Pitborn's overhead swing.
// Every 0.05 s it scans all active renderers; the FIRST time it sees a renderer whose bounds are larger than 20 m (diagonal) after the arm time, it logs one line:
// [BoundsDiag] t=<s> <name> path=<hierarchy> size=<m> centre=<xyz> camDist=<m> type=<renderer type> layer=<n> mesh=<name> under=<fighter name or ->.
// Also logs renderers whose bounds changed from <= 20 m to > 20 m (a stretched mesh). Nothing happens without the flag. No allocation per frame beyond the scan list.
public sealed class DiagRendererBounds : MonoBehaviour
{
    const float LimitM = 20f;
    float armAt = 25f, nextScan; readonly HashSet<Renderer> seen = new HashSet<Renderer>(); readonly Dictionary<Renderer, float> lastSize = new Dictionary<Renderer, float>(); int logged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("-boundsdiag", System.StringComparison.OrdinalIgnoreCase)) continue;
            var g = new GameObject("DiagRendererBounds"); DontDestroyOnLoad(g); var d = g.AddComponent<DiagRendererBounds>();
            int i = a.IndexOf(':'); float v; if (i > 0 && float.TryParse(a.Substring(i + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) d.armAt = v;
            Debug.Log("[BoundsDiag] armed, logs renderers with bounds > " + LimitM + " m after " + d.armAt + " s");
            return;
        }
    }

    static string Path(Transform t) { var sb = new StringBuilder(); int n = 0; while (t != null && n++ < 8) { sb.Insert(0, t.name + (sb.Length > 0 ? "/" : "")); t = t.parent; } return sb.ToString(); }
    static string Under(Transform t) { while (t != null) { var f = t.GetComponent<DuelFighter>(); if (f != null) return f.name; t = t.parent; } return "-"; }

    void Update()
    {
        if (Time.realtimeSinceStartup < armAt || Time.realtimeSinceStartup < nextScan || logged > 400) return; nextScan = Time.realtimeSinceStartup + 0.05f;
        var cam = Camera.main; var all = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var r in all)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue; float s = r.bounds.size.magnitude;
            float prev; bool had = lastSize.TryGetValue(r, out prev); lastSize[r] = s;
            bool big = s > LimitM, grew = had && prev <= LimitM && big;
            if (!big || (seen.Contains(r) && !grew)) continue; seen.Add(r); logged++;
            string mesh = "-"; var mf = r.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) mesh = mf.sharedMesh.name; else { var sm = r as SkinnedMeshRenderer; if (sm != null && sm.sharedMesh != null) mesh = sm.sharedMesh.name; }
            Debug.Log("[BoundsDiag] t=" + Time.realtimeSinceStartup.ToString("F2") + " " + r.name + " path=" + Path(r.transform) + " size=" + s.ToString("F1") + " centre=" + r.bounds.center.ToString("F1") + " camDist=" + (cam != null ? Vector3.Distance(cam.transform.position, r.bounds.center).ToString("F1") : "?") + " type=" + r.GetType().Name + " layer=" + r.gameObject.layer + " mesh=" + mesh + " under=" + Under(r.transform) + (grew ? " GREW from " + prev.ToString("F1") : ""));
        }
    }
}
