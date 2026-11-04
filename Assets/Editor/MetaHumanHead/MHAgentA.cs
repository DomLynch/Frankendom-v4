#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MetaHumanRig;

public static class MHAgentA
{
    static string Arg(string name, string def) { var a = System.Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1]; return def; }

    static void Save(Camera cam, int N, string path)
    {
        var rt = new RenderTexture(N, N, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); cam.targetTexture = rt; for (int i = 0; i < 4; i++) cam.Render();
        RenderTexture.active = rt; var png = new Texture2D(N, N, TextureFormat.RGB24, false); png.ReadPixels(new Rect(0, 0, N, N), 0, 0); png.Apply();
        File.WriteAllBytes(path, png.EncodeToPNG()); RenderTexture.active = null; cam.targetTexture = null; rt.Release();
    }

    static MHHeadBuilder Setup(out Camera cam)
    {
        AssetDatabase.Refresh();
        EditorSceneManager.OpenScene("Assets/Scenes/MHTest.unity");
        var b = Object.FindAnyObjectByType<MHHeadBuilder>(); cam = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        b.headMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MetaHumanHead/HDRP/MH_Skin_HDRP.mat"); b.teethMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MetaHumanHead/HDRP/MH_HDRP_teeth.mat");
        b.lod = int.Parse(Arg("-mhlod", "0")); b.Build(); b.EvaluateNow(); b.EvaluateNow();
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.transform.parent == null) l.gameObject.SetActive(false);
        b.ApplyStudioRig(cam); foreach (var h in Arg("-mhhide", "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)) foreach (var r in b.GetComponentsInChildren<Renderer>()) if (r.name.StartsWith(h)) r.enabled = false; return b;
    }

    public static void Render()
    {
        string name = Arg("-mhname", "hdrp_neutral_A2"); Directory.CreateDirectory("TestOut");
        Camera cam; var b = Setup(out cam);
        Save(cam, 1024, "TestOut/" + name + ".png");
        Renderer le = null, re = null; foreach (var r in b.GetComponentsInChildren<Renderer>()) { if (r.name.StartsWith("eyeLeft_")) le = r; if (r.name.StartsWith("eyeRight_")) re = r; }
        var c = (le.bounds.center + re.bounds.center) * 0.5f; float dist = float.Parse(Arg("-mheyedist", "0.3"), System.Globalization.CultureInfo.InvariantCulture);
        cam.transform.position = c - cam.transform.forward * dist; Save(cam, 1024, "TestOut/eyes_crop_" + name + ".png");
        var oc = le.bounds.center; cam.transform.position = oc - cam.transform.forward * (dist * 0.45f); Save(cam, 1024, "TestOut/eye_one_" + name + ".png");
        string refp = Arg("-mhref", "");
        if (refp != "" && File.Exists(refp))
        {
            var u = new Texture2D(2, 2); u.LoadImage(File.ReadAllBytes(refp)); var me = new Texture2D(2, 2); me.LoadImage(File.ReadAllBytes("TestOut/" + name + ".png"));
            var rt = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); var sbs = new Texture2D(2048, 1024, TextureFormat.RGB24, false);
            Graphics.Blit(u, rt); RenderTexture.active = rt; sbs.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
            Graphics.Blit(me, rt); RenderTexture.active = rt; sbs.ReadPixels(new Rect(0, 0, 1024, 1024), 1024, 0); sbs.Apply(); RenderTexture.active = null;
            File.WriteAllBytes("TestOut/sbs_" + name + ".png", sbs.EncodeToPNG());
        }
        Debug.Log("[MHAgentA] render ok " + name);
        EditorApplication.Exit(0);
    }

    public static void Render34()
    {
        string name = Arg("-mhname", "a3_34"); float yaw = float.Parse(Arg("-mhyaw", "25"), System.Globalization.CultureInfo.InvariantCulture); Directory.CreateDirectory("TestOut");
        Camera cam; var b = Setup(out cam);
        Renderer le = null, re = null; foreach (var r in b.GetComponentsInChildren<Renderer>()) { if (r.name.StartsWith("eyeLeft_")) le = r; if (r.name.StartsWith("eyeRight_")) re = r; }
        var c = (le.bounds.center + re.bounds.center) * 0.5f; cam.transform.RotateAround(c, Vector3.up, yaw);   // lights stay fixed in the world = head turned relative to the rig
        Save(cam, 1024, "TestOut/" + name + ".png"); Debug.Log("[MHAgentA] render34 ok " + name); EditorApplication.Exit(0);
    }

    /// <summary>A5: orbit views about the head centre. -mhviews "front:0:0,side:90:0,back34:150:20,top:0:80" (name:yawDeg:pitchDeg), -mhdist metres (default 1.2), -mhfov (default look fov), -mhlod, -mhname prefix.</summary>
    public static void RenderViews()
    {
        string name = Arg("-mhname", "a5v"); Directory.CreateDirectory("TestOut");
        Camera cam; var b = Setup(out cam);
        float dist = float.Parse(Arg("-mhdist", "1.2"), System.Globalization.CultureInfo.InvariantCulture); float fov = float.Parse(Arg("-mhfov", "0"), System.Globalization.CultureInfo.InvariantCulture); if (fov > 0f) cam.fieldOfView = fov;
        Bounds hb = new Bounds(); bool any = false; foreach (var r in b.GetComponentsInChildren<SkinnedMeshRenderer>()) if (r.name.StartsWith("head_")) { hb = r.bounds; any = true; }
        Renderer le = null, re = null; foreach (var r in b.GetComponentsInChildren<Renderer>()) { if (r.name.StartsWith("eyeLeft_")) le = r; if (r.name.StartsWith("eyeRight_")) re = r; }
        var eyeC = (le.bounds.center + re.bounds.center) * 0.5f; var c = any ? new Vector3(eyeC.x, hb.center.y + 0.35f * hb.extents.y, hb.center.z) : eyeC;
        var rot0 = cam.transform.rotation;
        foreach (var spec in Arg("-mhviews", "front:0:0,side:90:0,back34:150:20,top:0:80").Split(','))
        {
            var p = spec.Split(':'); float yaw = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), pitch = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
            cam.transform.rotation = rot0; cam.transform.position = c - rot0 * Vector3.forward * dist;
            cam.transform.RotateAround(c, Vector3.up, yaw); cam.transform.RotateAround(c, cam.transform.right, pitch);   // lights stay fixed in the world
            Save(cam, 1024, "TestOut/" + name + "_" + p[0] + ".png");
        }
        Debug.Log("[MHAgentA] views ok " + name); EditorApplication.Exit(0);
    }

    public static void RebuildViews() { MHRebuildAll.RebuildAllFromCanonical(); RenderViews(); }

    public static void Dump()
    {
        Camera cam; var b = Setup(out cam); string o = "";
        foreach (var r in b.GetComponentsInChildren<Renderer>()) { var sm = r as SkinnedMeshRenderer; o += r.name + " mat=" + (r.sharedMaterial ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "null") + " bounds=" + r.bounds.center.ToString("F4") + " ext=" + r.bounds.extents.ToString("F4") + (sm && sm.sharedMesh ? " verts=" + sm.sharedMesh.vertexCount : "") + "\n"; }
        Directory.CreateDirectory("TestOut"); File.WriteAllText("TestOut/dump_A.txt", o); EditorApplication.Exit(0);
    }

    public static void Tests()
    {
        Camera cam; var b = Setup(out cam);
        var res = MHRigTests.RunAll("MetaHumanHead", b); Directory.CreateDirectory("TestOut"); File.WriteAllText("TestOut/editor_results_A.json", MHRigTests.ToJson(res));
        foreach (var r in res) Debug.Log("[MHTEST] " + r.status + " " + r.name + " " + r.detail);
        EditorApplication.Exit(res.Exists(r => r.status == "FAIL") ? 1 : 0);
    }
}
#endif
