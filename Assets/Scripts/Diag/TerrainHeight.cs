using System.Text;
using UnityEngine;
// TerrainHeight (unity-dev2): -terrainheight:x,z;x,z[:delaySeconds] logs the ground height (down ray from y 300, all colliders, plus the highest renderer bounds top under the point) at each x,z, for explicit y of runtime prop rows. No effect without the arg.
public class TerrainHeight : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs())
            if (a.StartsWith("-terrainheight:")) { var g = new GameObject("TerrainHeight"); DontDestroyOnLoad(g); var t = g.AddComponent<TerrainHeight>(); var p = a.Substring(15).Split(new[] { (char)58 }); t.pts = p[0]; t.delay = p.Length > 1 ? float.Parse(p[1]) : 25f; }
    }
    public string pts = ""; public float delay = 25f; float next = -1f; int n;
    void Update()
    {
        if (next < 0f) next = Time.unscaledTime + delay; if (Time.unscaledTime < next || n >= 2) return; next = Time.unscaledTime + 20f; n++;
        var sb = new StringBuilder("[TerrainHeight]\n");
        foreach (var s in pts.Split(new[] { (char)59 }))
        {
            var xz = s.Split(new[] { (char)44 }); if (xz.Length < 2) continue; float x = float.Parse(xz[0]), z = float.Parse(xz[1]);
            var hits = Physics.RaycastAll(new Vector3(x, 300f, z), Vector3.down, 600f, ~0, QueryTriggerInteraction.Ignore); float best = -999f; string nm = "none";
            foreach (var h in hits) { if (h.point.y > best) { best = h.point.y; nm = h.collider.name; } }
            sb.AppendLine("  x " + x + " z " + z + " -> y " + best.ToString("F2") + " (" + nm + ", " + hits.Length + " hits)");
        }
        Debug.Log(sb.ToString());
    }
}
