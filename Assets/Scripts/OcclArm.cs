#if !FRANKEN_NO_HARNESS
using UnityEngine;

// OCCLV2 (laneOCCL): harness-only A/B switch so occlusion culling can be paired inside ONE build (no second 17-min build, no machine drift between builds).
//   -occl:0  every camera runs with useOcclusionCulling = false   (arm OFF)
//   -occl:1  every camera runs with useOcclusionCulling = true    (arm ON, the baked default)
// No argument = this component is never created, nothing changes. Cameras named in occlusion.json camera.offNames (FinisherCam, FreeCam, ShotCam) stay off even with -occl:1.
// Logs one line per change: "[OcclArm] ...". Compiled out with FRANKEN_NO_HARNESS.
public sealed class OcclArm : MonoBehaviour
{
    const string Arg = "-occl:";
    static readonly string[] OffNames = { "FinisherCam", "FreeCam", "ShotCam" };   // mirrors occlusion.json camera.offNames (editor-only json, not readable in a player)
    Camera[] buf = new Camera[0];
    bool want; int lastOn = -1, lastCams = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs())
            if (a.StartsWith(Arg, System.StringComparison.OrdinalIgnoreCase))
            {
                var go = new GameObject("OcclArm"); DontDestroyOnLoad(go);
                var c = go.AddComponent<OcclArm>(); c.want = a.Substring(Arg.Length) != "0";
                return;
            }
    }

    void LateUpdate()
    {
        int n = Camera.allCamerasCount;
        if (buf.Length < n) buf = new Camera[n];
        n = Camera.GetAllCameras(buf);
        int on = 0;
        for (int i = 0; i < n; i++)
        {
            var c = buf[i]; if (c == null) continue;
            bool v = want && System.Array.IndexOf(OffNames, c.name) < 0;
            if (c.useOcclusionCulling != v) c.useOcclusionCulling = v;
            if (v) on++;
        }
        if (on != lastOn || n != lastCams) { lastOn = on; lastCams = n; Debug.Log("[OcclArm] arm " + (want ? "ON" : "OFF") + ": " + on + " of " + n + " cameras use occlusion culling"); }
    }
}
#endif
