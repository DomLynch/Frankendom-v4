using UnityEngine; using UnityEditor; using System.Text;
public static class HdrpBoulderCheck {
    public static void Run() {
        var sb = new StringBuilder(); int n = 0, bad = 0;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) {
            var root = r.transform.root; string nm = r.transform.name.ToLower() + root.name.ToLower();
            if (!nm.Contains("boulder") || r is ParticleSystemRenderer) continue;
            var b = r.bounds; var o = new Vector3(b.center.x, b.max.y + 5f, b.center.z); float bottom = b.min.y; float gy = float.NaN;
            foreach (var h in Physics.RaycastAll(o, Vector3.down, 60f)) { if (h.collider.transform.IsChildOf(r.transform) || r.transform.IsChildOf(h.collider.transform)) continue; if (float.IsNaN(gy) || h.point.y > gy) gy = h.point.y; }
            n++; float d = float.IsNaN(gy) ? float.NaN : bottom - gy; bool ok = !float.IsNaN(d) && d < 0.3f && d > -0.6f; if (!ok) bad++;
            sb.AppendLine(r.transform.name + " bottom=" + bottom.ToString("F2") + " ground=" + gy.ToString("F2") + " gap=" + d.ToString("F2") + (ok ? "" : " CHECK"));
        }
        System.IO.File.WriteAllText("Shots/_boulders.txt", "count=" + n + " check=" + bad + "\n" + sb);
    }
}
