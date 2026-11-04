using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

public static class TestShot
{
    public static string Run()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/HadesSlice.unity")
            scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");

        var cam = Camera.main;
        if (cam == null) return "no camera";

        foreach (var mb in cam.GetComponents<MonoBehaviour>())
            if (mb != null && mb.GetType().Name == "CameraRig") mb.enabled = false;

        // Pose characters
        var drac = GameObject.Find("Dracula");
        var pit = GameObject.Find("Pitborn");

        void PoseCharacter(GameObject go, string clipName, float normTime)
        {
            if (go == null) return;
            var a = go.GetComponentInChildren<Animator>();
            if (a == null) return;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            a.Play(clipName, 0, normTime);
            a.Update(0.1f);

            // Apply GripPose if present
            var gp = go.GetComponentInChildren<GripPose>();
            if (gp != null)
            {
                // Find finger bones and curl them
                string[] fingers = { "Index", "Middle", "Ring", "Pinky" };
                var all = go.GetComponentsInChildren<Transform>(true);
                for (int f = 0; f < 5; f++)
                {
                    string nm = f < 4 ? fingers[f] : "Thumb";
                    for (int j = 1; j <= 3; j++)
                    {
                        foreach (var t in all)
                        {
                            if (t.name == "mixamorig:RightHand" + nm + j)
                            {
                                float deg = f < 4 ? gp.fingerDeg : gp.thumbDeg;
                                t.localRotation = t.localRotation * Quaternion.AngleAxis(deg * (f < 4 ? 1f : 0.7f), f < 4 ? Vector3.forward : Vector3.up);
                                break;
                            }
                        }
                    }
                }
            }
        }

        PoseCharacter(drac, "guard_idle", 0.15f);
        PoseCharacter(pit, "guard_idle", 0.15f);

        Directory.CreateDirectory("Shots");

        (string name, Vector3 pos, Vector3 target, float fov)[] views = {
            ("hdrp_v5_game", new Vector3(1.0f, 3.6f, 6.1f), new Vector3(0.2f, 1.2f, 14.2f), 38.6f),
            ("hdrp_v5_gate", new Vector3(0.5f, 1.7f, 10.5f), new Vector3(0f, 4.5f, 24f), 62f)
        };

        foreach (var v in views)
        {
            cam.transform.position = v.pos;
            cam.transform.LookAt(v.target);
            cam.fieldOfView = v.fov;

            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGBHalf);
            cam.targetTexture = rt;
            for (int i = 0; i < 30; i++) cam.Render();

            RenderTexture.active = rt;
            var t = new Texture2D(1920, 1080, TextureFormat.RGBAHalf, false);
            t.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);

            var px = t.GetPixels();
            var t8 = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color(Mathf.Clamp01(px[i].r), Mathf.Clamp01(px[i].g), Mathf.Clamp01(px[i].b), 1f);
            t8.SetPixels(px);

            string outPath = Path.Combine("Shots", v.name + ".png");
            File.WriteAllBytes(outPath, t8.EncodeToPNG());

            cam.targetTexture = null;
            RenderTexture.active = null;
            rt.Release();
        }

        return "SUCCESS";
    }
}
