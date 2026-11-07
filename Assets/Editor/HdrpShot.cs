using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

public static class HdrpShot
{
    public static void Run() { Run(true); }
    public static void Run(bool reopen)
    {
        if (reopen) EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var cam = Camera.main; if (cam == null) { Debug.Log("HDRPSHOT no camera"); return; }
        foreach (var mb in cam.GetComponents<MonoBehaviour>()) if (mb != null && mb.GetType().Name == "CameraRig") mb.enabled = false;
        Directory.CreateDirectory("Shots"); var camPos0 = cam.transform.position; var camRot0 = cam.transform.rotation; var fov0 = cam.fieldOfView;
        foreach (var an in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None)) { if (an.runtimeAnimatorController == null) continue; an.Rebind(); an.Play("idle", 0, 0.1f); an.Update(0.05f); }   // pose the fighters in their idle stance instead of the T-pose
        foreach (var ps in Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)) { if (ps.transform.root.name != "FurnaceRoot" && !ps.transform.IsChildOf(GameObject.Find("FurnaceRoot").transform)) continue; ps.Simulate(5f, true, true, true); ps.Play(); }   // particles do not tick in edit mode: pre-warm so stills show flames/smoke/embers
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) { var h = l.GetComponent<HDAdditionalLightData>(); Debug.Log($"HDRPLIGHT {l.name} {l.type} intensity={l.intensity:F1} unit={l.lightUnit} shadows={l.shadows} enabled={l.enabled} hd={(h != null)}"); }
        var evs = System.Environment.GetEnvironmentVariable("HDRP_EV"); if (!string.IsNullOrEmpty(evs)) { var vol = Object.FindFirstObjectByType<UnityEngine.Rendering.Volume>(); if (vol && vol.sharedProfile.TryGet<Exposure>(out var exo)) { exo.fixedExposure.value = float.Parse(evs); Debug.Log("HDRPSHOT EV override " + evs); } }
        Debug.Log($"HDRPSHOT gfx={SystemInfo.graphicsDeviceType} dev={SystemInfo.graphicsDeviceName} rt={SystemInfo.supportsRayTracing} pipeline={UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline}");
        (string, Vector3, Vector3, float)[] views = {
            ("hdrp_game", new Vector3(1.0f, 3.6f, 6.1f), new Vector3(0.2f, 1.2f, 14.2f), 38.6f),
            ("hdrp_gate", new Vector3(0.5f, 1.7f, 10.5f), new Vector3(0f, 4.5f, 24f), 62f),
            ("hdrp_wide", new Vector3(-16f, 7f, -10f), new Vector3(0f, 4f, 18f), 60f), ("hdrp_boulder", new Vector3(-12f, 2.5f, -5.5f), new Vector3(-13.5f, -0.5f, -1f), 50f) };
        foreach (var v in views)
        {
            cam.transform.position = v.Item2; cam.transform.LookAt(v.Item3); cam.fieldOfView = v.Item4;
            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGBHalf); cam.targetTexture = rt;
            for (int i = 0; i < 30; i++) cam.Render();   // let TAA / volumetrics / exposure settle
            RenderTexture.active = rt; var t = new Texture2D(1920, 1080, TextureFormat.RGBAHalf, false); t.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            var px = t.GetPixels(); var t8 = new Texture2D(1920, 1080, TextureFormat.RGB24, false); for (int i = 0; i < px.Length; i++) px[i] = new Color(Mathf.Clamp01(px[i].r), Mathf.Clamp01(px[i].g), Mathf.Clamp01(px[i].b), 1f); t8.SetPixels(px);
            File.WriteAllBytes("Shots/" + v.Item1 + ".png", t8.EncodeToPNG()); File.WriteAllText("Shots/" + v.Item1 + ".png.editmode.json", "{\"capture\":\"editmode\",\"tier\":\"none\",\"note\":\"GATE2K: HdrpShot.cs, scene opened in the Editor, cam.Render x30. QualityTiers / RuntimeQuality / runtime_flags never ran. Not a player frame, not a look gate, not a baseline.\"}");   // GATE2K
            cam.targetTexture = null; RenderTexture.active = null; rt.Release();
        }
        // leave the scene exactly as found: the rig must run in the player (a saved disabled CameraRig gave Dom a far wide camera)
        foreach (var mb in cam.GetComponents<MonoBehaviour>()) if (mb != null && mb.GetType().Name == "CameraRig") mb.enabled = true;
        cam.transform.SetPositionAndRotation(camPos0, camRot0); cam.fieldOfView = fov0; cam.targetTexture = null;
        Debug.Log("HDRPSHOT done (EDIT-MODE shots: not player frames, not a look gate or baseline; sidecar .editmode.json written)");   // GATE2K
    }
}