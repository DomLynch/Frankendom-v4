using System.Text;
using UnityEngine;
// LightDump (unity-dev2): -lightdump[:seconds] logs every active Light near the camera once, to find unknown pools. No effect without the arg.
public class LightDump : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs())
            if (a.StartsWith("-lightoff:")) { var g2 = new GameObject("LightOff"); DontDestroyOnLoad(g2); var c2 = g2.AddComponent<LightDump>(); c2.delay = 1e9f; c2.off = a.Substring(10).Split(new[] {(char)44}); }
            else if (a.StartsWith("-lightdump")) { var g = new GameObject("LightDump"); DontDestroyOnLoad(g); g.AddComponent<LightDump>().delay = a.Contains(":") ? float.Parse(a.Substring(a.IndexOf(':') + 1)) : 40f; }
    }
    public float delay = 40f; float next, nextOff; int n; string[] off;
    void Update()
    {
        if (off != null && Time.unscaledTime >= nextOff) { nextOff = Time.unscaledTime + 0.5f; foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) foreach (var o in off) if (l.enabled && l.name == o) l.enabled = false; }
        if (off != null) return;
        if (Time.unscaledTime < next || n > 3) return; next = Time.unscaledTime + delay; n++;
        var cam = Camera.main; Vector3 c = cam != null ? cam.transform.position : Vector3.zero;
        var sb = new StringBuilder("[LightDump] camera " + c.ToString("F1") + "\n");
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!l.enabled) continue; float d = Vector3.Distance(l.transform.position, c); if (d > 80f && l.type != LightType.Directional) continue;
            var hd = l.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            string path = l.name; var t = l.transform.parent; for (int i = 0; i < 3 && t != null; i++) { path = t.name + "/" + path; t = t.parent; }
            sb.AppendLine("  d=" + d.ToString("F0") + " " + l.type + " " + path + " pos " + l.transform.position.ToString("F1") + " int " + (hd != null ? hd.intensity : l.intensity) + " range " + l.range + " col " + l.color + " mask " + l.renderingLayerMask + " rot " + l.transform.eulerAngles.ToString("F0") + " shadows " + l.shadows);
        }
        Debug.Log(sb.ToString());
    }
}

