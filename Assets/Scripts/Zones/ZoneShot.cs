using System;
using System.Collections;
using System.IO;
using UnityEngine;

// CAMPGROUND v1 (laneCAMPGROUND): -zoneshot:<dir>. Game-camera PNGs inside a ZONES run (the harness modes switch zones off). Launch: HadesSlice.exe -zones -zoneshot:<dir>  (add the zone_save start with play_zone_at.ps1 if wanted).
// Shots are DATA (zone_shots.json): per shot the player STANDS at a zones.json spot (teleport, so the rings / NPCs / creature of that place wake as in play), the game camera is put at a spot + offset, looks at a spot + offset,
// is rendered into a RenderTexture (Camera.Render, NO screen grab: no desktop, no overlay, no window in the picture), read back and written as PNG with a .json sidecar. Upright: ReadPixels of a RenderTexture is bottom-up,
// which EncodeToPNG writes upright; zone_shots.json "flipY" turns it over if a build proves otherwise, and the sidecar logs the mean luma of the top / bottom tenth for a one-look check. Never boots without the flag.
// Quits when done (data quit). UNPROVEN IN UNITY (Camera.Render under HDRP from a coroutine is the part to watch).
[Serializable] public class ZsOff { public string spot = ""; public float[] off = new float[3]; }
[Serializable] public class ZsShot { public string id = "", zone = "", stand = ""; public ZsOff cam = new ZsOff(), look = new ZsOff(); public float fov = 40f, settleSeconds = 2f, waitNpcSeconds = 0f; }
[Serializable] public class ZsCfg { public int width = 1920, height = 1080, msaa = 1; public bool flipY, quit = true; public float startWaitSeconds = 60f, groundWaitSeconds = 20f; public ZsShot[] shots = new ZsShot[0]; }

public sealed class ZoneShot : MonoBehaviour
{
    static string dir; ZsCfg C; DuelFighter player; int ok, fail;
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        dir = Arg("-zoneshot:"); if (string.IsNullOrEmpty(dir)) return;
        if (!ZoneSave.Active) { Debug.LogWarning("[ZoneShot] zones are off (needs -zones, and no harness flag): nothing to do"); return; }
        var go = new GameObject("ZoneShot"); DontDestroyOnLoad(go); go.AddComponent<ZoneShot>();
    }

    void Start()
    {
        C = new ZsCfg(); var t = HudCfg.ReadData("zone_shots.json");
        if (t != null) { try { var k = JsonUtility.FromJson<ZsCfg>(t); if (k != null) C = k; } catch (Exception e) { Debug.LogWarning("[ZoneShot] zone_shots.json unreadable: " + e.Message); } }
        StartCoroutine(Run());
    }
    static void Log(string s) { Debug.Log("[ZoneShot] " + s); }

    IEnumerator Run()
    {
        try { Directory.CreateDirectory(dir); } catch (Exception e) { Debug.LogWarning("[ZoneShot] cannot make " + dir + ": " + e.Message); yield break; }
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < C.startWaitSeconds && (!ZoneRuntime.Owns || GameLoop.Player == null || ZoneGroundHold.Active || Camera.main == null)) yield return null;
        player = GameLoop.Player; if (player == null || Camera.main == null) { Debug.LogWarning("[ZoneShot] no player / camera: zones did not start"); Finish(); yield break; }
        foreach (var s in C.shots) { if (s == null) continue; yield return Shoot(s); }
        Log("done: " + ok + " ok, " + fail + " failed, dir " + dir);
        try { File.WriteAllText(Path.Combine(dir, "zoneshot_done.txt"), ok + " ok " + fail + " failed\n"); } catch (Exception) { }
        Finish();
    }
    void Finish() { if (C.quit) Application.Quit(); }

    static bool SpotWorld(string zone, string spot, float[] off, out Vector3 w)
    {
        w = Vector3.zero; var sp = ZoneData.Spot(ZoneData.Zone(zone), spot); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        float gy, d; if (!ZoneGround.Probe(new Vector3(sp.pos[0], 2f, sp.pos[1]), null, out gy, out d)) gy = 0f;
        w = new Vector3(sp.pos[0], gy, sp.pos[1]) + (off != null && off.Length >= 3 ? new Vector3(off[0], off[1], off[2]) : Vector3.zero); return true;
    }

    IEnumerator Shoot(ZsShot s)
    {
        var st = ZoneData.Spot(ZoneData.Zone(s.zone), s.stand);
        if (st == null || st.pos == null || st.pos.Length < 2) { Debug.LogWarning("[ZoneShot] " + s.id + ": stand spot '" + s.stand + "' not in zone '" + s.zone + "'"); fail++; yield break; }
        Vector3 pos = new Vector3(st.pos[0], player.transform.position.y, st.pos[1]);
        var cc = player.GetComponent<CharacterController>(); bool en = cc != null && cc.enabled; if (en) cc.enabled = false;
        player.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, st.yaw, 0f)); if (en) cc.enabled = true; Physics.SyncTransforms();
        ZoneGround.Begin(player, pos);
        float t0 = Time.realtimeSinceStartup;
        while (ZoneGroundHold.Active && Time.realtimeSinceStartup - t0 < C.groundWaitSeconds) yield return null;
        float tw = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - tw < s.settleSeconds) yield return null;
        if (s.waitNpcSeconds > 0f) { tw = Time.realtimeSinceStartup; while (HubNpcs.Standing == 0 && Time.realtimeSinceStartup - tw < s.waitNpcSeconds) yield return null; yield return null; yield return null; }
        yield return new WaitForEndOfFrame();
        Vector3 from, at;
        if (!SpotWorld(s.zone, s.cam.spot, s.cam.off, out from) || !SpotWorld(s.zone, s.look.spot, s.look.off, out at)) { Debug.LogWarning("[ZoneShot] " + s.id + ": camera / look spot missing"); fail++; yield break; }
        var cam = Camera.main; var keepPos = cam.transform.position; var keepRot = cam.transform.rotation; float keepFov = cam.fieldOfView; var keepRt = cam.targetTexture;
        var rt = new RenderTexture(C.width, C.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = Mathf.Max(1, C.msaa) };
        Texture2D tex = null;
        try
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation((at - from).normalized, Vector3.up)); cam.fieldOfView = s.fov; cam.targetTexture = rt; cam.Render();
            cam.targetTexture = keepRt; var prev = RenderTexture.active; RenderTexture.active = rt;
            tex = new Texture2D(C.width, C.height, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, C.width, C.height), 0, 0); tex.Apply(); RenderTexture.active = prev;
            float lt = Luma(tex, 0.9f, 1f), lb = Luma(tex, 0f, 0.1f);
            if (C.flipY) Flip(tex);
            string png = Path.Combine(dir, "zoneshot_" + s.id + ".png"); File.WriteAllBytes(png, tex.EncodeToPNG());
            float gy, gd; bool g = ZoneGround.Probe(player.transform.position + Vector3.up * 0.1f, player, out gy, out gd);
            File.WriteAllText(png + ".json", "{\"harness\":\"zoneshot\",\"id\":\"" + s.id + "\",\"zone\":\"" + s.zone + "\",\"camPos\":\"" + from.ToString("F2") + "\",\"lookAt\":\"" + at.ToString("F2") + "\",\"fov\":" + s.fov.ToString("F1") + ",\"playerY\":" + player.transform.position.y.ToString("F2") + ",\"ground\":" + (g ? "true" : "false") + ",\"lumaTopTenth\":" + lt.ToString("F3") + ",\"lumaBottomTenth\":" + lb.ToString("F3") + ",\"flipY\":" + (C.flipY ? "true" : "false") + ",\"npcs\":" + HubNpcs.Standing + "}\n");
            Log(s.id + ": " + png + " cam " + from.ToString("F1") + " player y " + player.transform.position.y.ToString("F2") + " ground " + (g ? "yes" : "NO") + " npcs " + HubNpcs.Standing + " luma top/bottom " + lt.ToString("F2") + "/" + lb.ToString("F2")); ok++;
        }
        catch (Exception e) { Debug.LogWarning("[ZoneShot] " + s.id + " failed: " + e.Message); fail++; cam.targetTexture = keepRt; }
        finally { cam.transform.SetPositionAndRotation(keepPos, keepRot); cam.fieldOfView = keepFov; rt.Release(); Destroy(rt); if (tex != null) Destroy(tex); }
    }

    static float Luma(Texture2D t, float y0, float y1)
    {
        int h = t.height, w = t.width, a = Mathf.Clamp((int)(y0 * h), 0, h - 1), b = Mathf.Clamp((int)(y1 * h), a + 1, h); double sum = 0; int n = 0;
        for (int y = a; y < b; y += 8) for (int x = 0; x < w; x += 8) { var c = t.GetPixel(x, y); sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; n++; }
        return n > 0 ? (float)(sum / n) : 0f;
    }
    static void Flip(Texture2D t)
    {
        int w = t.width, h = t.height; var px = t.GetPixels32(); var o = new Color32[px.Length];
        for (int y = 0; y < h; y++) Array.Copy(px, y * w, o, (h - 1 - y) * w, w);
        t.SetPixels32(o); t.Apply();
    }
}
