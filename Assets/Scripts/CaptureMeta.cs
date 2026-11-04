using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Capture QC guard (Goose): every PNG written under -out:<dir> gets a sibling .json (flags file, fast switch, exposure, camera position + FOV, time),
// and a "FAST/NOT-QC" stamp is drawn on screen while -fast is active. Runs only when -out: is on the command line.
public class CaptureMeta : MonoBehaviour
{
    static string dir; float next;

    public static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-out:")) dir = a.Substring(5);
        if (string.IsNullOrEmpty(dir) || FindFirstObjectByType<CaptureMeta>() != null) return;
        var go = new GameObject("CaptureMeta"); DontDestroyOnLoad(go); go.AddComponent<CaptureMeta>();
    }

    void OnGUI()
    {
        if (!RuntimeQuality.Fast) return;
        var s = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold }; s.normal.textColor = Color.yellow;
        GUI.Label(new Rect(Screen.width - 260, Screen.height - 44, 250, 36), "FAST/NOT-QC", s);
    }

    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + 1f;
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var png in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories))
            {
                string js = Path.ChangeExtension(png, ".json"); if (File.Exists(js)) continue;
                var cam = Camera.main; float ev = float.NaN, sky = float.NaN;
                foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None)) { var p = v.sharedProfile; if (p != null && p.TryGet(out Exposure ex)) ev = ex.fixedExposure.value; if (p != null && p.TryGet(out GradientSky gs)) sky = gs.exposure.value; }
                string cp = cam ? cam.transform.position.ToString("F2") : "n/a", fov = cam ? cam.fieldOfView.ToString("F1") : "n/a";
                File.WriteAllText(js, "{\"png\":\"" + Path.GetFileName(png) + "\",\"flagsFile\":\"" + RuntimeQuality.FlagsPath.Replace("\\", "/") + "\",\"fast\":" + (RuntimeQuality.Fast ? "true" : "false") + ",\"fixedExposureEV\":" + ev.ToString("F2") + ",\"skyExposure\":" + sky.ToString("F2") + ",\"cameraPos\":\"" + cp + "\",\"fov\":\"" + fov + "\",\"note\":\"camera = main camera when the sidecar was written (within ~1 s of the capture)\",\"time\":\"" + System.DateTime.Now.ToString("s") + "\"}");
            }
        }
        catch { }
    }
}
