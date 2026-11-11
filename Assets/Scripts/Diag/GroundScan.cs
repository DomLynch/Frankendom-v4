using System.Text;
using UnityEngine;
// GroundScan (unity-dev2): -groundscan[:delaySeconds] logs every Renderer whose bounds (XZ) contain the player's feet (name, parent chain, materials + shader, base colour, albedo texture, enabled).
// -rendoff:name,name disables every renderer whose GameObject name contains one of the names (toggle test). No effect without the args.
public class GroundScan : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string off = null; float delay = -1f;
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-groundscan")) delay = a.Contains(":") ? float.Parse(a.Substring(a.IndexOf(':') + 1)) : 20f;
            if (a.StartsWith("-rendoff:")) off = a.Substring(9);
        }
        if (delay < 0f && off == null) return;
        var g = new GameObject("GroundScan"); DontDestroyOnLoad(g); var s = g.AddComponent<GroundScan>(); s.delay = delay; s.off = off == null ? null : off.Split(new[] { (char)44 });
    }
    public float delay; public string[] off; float next = -1f, nextOff = 0f; int n;
    void Update()
    {
        if (off != null && Time.unscaledTime >= nextOff) { nextOff = Time.unscaledTime + 1f; foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) foreach (var o in off) if (r.enabled && r.gameObject.name.IndexOf(o, System.StringComparison.OrdinalIgnoreCase) >= 0) r.enabled = false; }
        if (delay < 0f) return; if (next < 0f) next = Time.unscaledTime + delay; if (Time.unscaledTime < next || n >= 2) return; next = Time.unscaledTime + 15f; n++;
        var p = GameLoop.Player; if (p == null) return; Vector3 f = p.transform.position;
        var sb = new StringBuilder("[GroundScan] feet " + f.ToString("F1") + "\n");
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var b = r.bounds; if (f.x < b.min.x || f.x > b.max.x || f.z < b.min.z || f.z > b.max.z) continue; if (b.max.y < f.y - 3f || b.min.y > f.y + 1f) continue;
            string chain = r.gameObject.name; var t = r.transform.parent; for (int i = 0; i < 4 && t != null; i++) { chain = t.name + "/" + chain; t = t.parent; }
            var ms = new StringBuilder();
            foreach (var m in r.sharedMaterials) { if (m == null) { ms.Append("null;"); continue; } string tex = ""; if (m.HasProperty("_BaseColorMap") && m.GetTexture("_BaseColorMap") != null) tex = m.GetTexture("_BaseColorMap").name; string col = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString("F2") : ""; ms.Append(m.name + "[" + (m.shader != null ? m.shader.name : "?") + " col " + col + " tex " + tex + "];"); }
            sb.AppendLine("  " + (r.enabled ? "ON " : "off") + " " + chain + " size " + b.size.ToString("F0") + " top " + b.max.y.ToString("F2") + " mats " + ms);
        }
        Debug.Log(sb.ToString());
    }
}
