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

    GUIStyle fastStyle; int fastSize = -1;   // UIDATA_C v1: the stamp style is built once (was a new GUIStyle every frame)
    void OnGUI()
    {
        if (!RuntimeQuality.Fast || Event.current.type != EventType.Repaint) return;
        var ZP = ZoneUiCfg.Get().capture;
        if (fastStyle == null || fastSize != ZP.fontSize) { fastSize = ZP.fontSize; fastStyle = new GUIStyle(GUI.skin.label) { fontSize = ZP.fontSize, fontStyle = FontStyle.Bold }; fastStyle.normal.textColor = ZoneUiCfg.Rgba(ZP.color, Color.yellow); }
        GUI.Label(new Rect(Screen.width - ZP.rightInset, Screen.height - ZP.bottomInset, ZP.w, ZP.h), "FAST/NOT-QC", fastStyle);
    }

    // WOUNDSHOT v84: the sidecar json of one PNG. `extra` = more json members ("a":1,"b":"x", no leading comma) appended by a harness that knows more than the scene (hit zone, damage, decal count ...).
    static string Build(string png, string extra)
    {
        var cam = Camera.main; float ev = float.NaN, sky = float.NaN;
        foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None)) { var p = v.sharedProfile; if (p != null && p.TryGet(out Exposure ex)) ev = ex.fixedExposure.value; if (p != null && p.TryGet(out GradientSky gs)) sky = gs.exposure.value; }
        string cp = cam ? cam.transform.position.ToString("F2") : "n/a", fov = cam ? cam.fieldOfView.ToString("F1") : "n/a";
        return "{\"png\":\"" + Path.GetFileName(png) + "\",\"flagsFile\":\"" + RuntimeQuality.FlagsPath.Replace("\\", "/") + "\",\"fast\":" + (RuntimeQuality.Fast ? "true" : "false") + ",\"fixedExposureEV\":" + ev.ToString("F2") + ",\"skyExposure\":" + sky.ToString("F2") + ",\"cameraPos\":\"" + cp + "\",\"fov\":\"" + fov + "\",\"note\":\"camera = main camera when the sidecar was written (within ~1 s of the capture)\",\"time\":\"" + System.DateTime.Now.ToString("s") + "\"" + (string.IsNullOrEmpty(extra) ? "" : "," + extra) + "}";
    }

    /// <summary>Write the sidecar next to `png` right now (so the 1 s scan in Update does not write the plain one first).</summary>
    public static void WriteFor(string png, string extra)
    {
        try { File.WriteAllText(Path.ChangeExtension(png, ".json"), Build(png, extra)); } catch { }
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
                File.WriteAllText(js, Build(png, null));
            }
        }
        catch { }
    }
}
