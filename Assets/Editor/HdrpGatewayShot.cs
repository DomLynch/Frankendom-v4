using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Edit-mode (batch) captures of the gateway states, same method as HdrpShot (camera.Render into a RenderTexture, no Play mode), but into Shots/gateway/ so it never overwrites
// another agent's pictures. States are applied with GatewayRift.PreviewState (throw-away material clones, scene is reopened afterwards: nothing is saved).
//   HdrpGatewayShot.Run()          abyss + rift + two opening frames, 4 views each (game, gate, gate_close, gate_side)  = 16 PNGs  (about 2 min)
//   HdrpGatewayShot.Abyss()        abyss only          HdrpGatewayShot.Rift()   rift only (fully open)
// Needs the GatewayRoot to exist (run HdrpGateway.Run() first). Edit-mode limits: the camera shake, sound, particles' live motion and the shard spin are Play-mode only.
public static class HdrpGatewayShot
{
    struct View { public string name; public Vector3 pos, look; public float fov; }
    struct St { public string name; public float p, flash; }

    static readonly View[] Views = {
        new View { name = "game",       pos = new Vector3(1.0f, 3.6f, 6.1f),  look = new Vector3(0.2f, 1.2f, 14.2f), fov = 38.6f },
        new View { name = "gate",       pos = new Vector3(0.5f, 1.7f, 10.5f), look = new Vector3(0f, 4.5f, 24f),     fov = 62f },
        new View { name = "gate_close", pos = new Vector3(0f, 2.6f, 17f),     look = new Vector3(0f, 4.4f, 27f),     fov = 45f },
        new View { name = "gate_side",  pos = new Vector3(-5f, 3.0f, 15f),    look = new Vector3(0f, 4.4f, 27f),     fov = 45f },
    };

    public static void Run() { Shoot(new[] { new St { name = "abyss", p = 0f }, new St { name = "open35", p = 0.35f, flash = 0.6f }, new St { name = "open65", p = 0.65f }, new St { name = "rift", p = 1f } }); }
    public static void Abyss() { Shoot(new[] { new St { name = "abyss", p = 0f } }); }
    public static void Rift() { Shoot(new[] { new St { name = "rift", p = 1f } }); }

    static void Shoot(St[] states)
    {
        var D = GatewayTable.Get();
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != D.scenePath) scene = EditorSceneManager.OpenScene(D.scenePath);
        var gr = Object.FindAnyObjectByType<GatewayRift>();
        if (gr == null) { Debug.LogError("HDRPGATEWAYSHOT no GatewayRoot in the scene: run HdrpGateway.Run() first"); return; }
        var cam = Camera.main; if (cam == null) { Debug.LogError("HDRPGATEWAYSHOT no main camera"); return; }
        Directory.CreateDirectory("Shots/gateway");
        foreach (var mb in cam.GetComponents<MonoBehaviour>()) if (mb != null && mb.GetType().Name == "CameraRig") mb.enabled = false;
        foreach (var an in Object.FindObjectsByType<Animator>()) { if (an.runtimeAnimatorController == null) continue; an.Rebind(); an.Play("idle", 0, 0.1f); an.Update(0.05f); }
        var fr = GameObject.Find("FurnaceRoot");
        if (fr != null) foreach (var ps in fr.GetComponentsInChildren<ParticleSystem>()) { ps.Simulate(5f, true, true, true); ps.Play(); }
        var pos0 = cam.transform.position; var rot0 = cam.transform.rotation; var fov0 = cam.fieldOfView;

        foreach (var st in states)
        {
            gr.PreviewState(st.p, st.flash);
            foreach (var v in Views)
            {
                cam.transform.position = v.pos; cam.transform.LookAt(v.look); cam.fieldOfView = v.fov;
                var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGBHalf); cam.targetTexture = rt;
                for (int i = 0; i < 30; i++) cam.Render();   // let TAA / volumetrics / exposure settle
                RenderTexture.active = rt;
                var t = new Texture2D(1920, 1080, TextureFormat.RGBAHalf, false); t.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                var px = t.GetPixels(); var t8 = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
                for (int i = 0; i < px.Length; i++) px[i] = new Color(Mathf.Clamp01(px[i].r), Mathf.Clamp01(px[i].g), Mathf.Clamp01(px[i].b), 1f);
                t8.SetPixels(px);
                File.WriteAllBytes("Shots/gateway/" + st.name + "_" + v.name + ".png", t8.EncodeToPNG());
                cam.targetTexture = null; RenderTexture.active = null; rt.Release();
                Object.DestroyImmediate(t); Object.DestroyImmediate(t8); Object.DestroyImmediate(rt);
            }
            Debug.Log("HDRPGATEWAYSHOT state " + st.name + " done");
        }
        cam.transform.SetPositionAndRotation(pos0, rot0); cam.fieldOfView = fov0; cam.targetTexture = null;
        EditorSceneManager.OpenScene(D.scenePath);   // discard the preview (clones, shards, scaled lights): nothing from this run is saved
        Debug.Log("HDRPGATEWAYSHOT done -> Shots/gateway/");
    }
}
