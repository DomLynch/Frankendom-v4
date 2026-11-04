using UnityEditor; using UnityEngine; using UnityEditor.SceneManagement;
public static class HdrpInspectWeapons
{
    static System.Collections.Generic.List<(Material, string, float)> restore = new System.Collections.Generic.List<(Material, string, float)>();
    static void SetAll(string match, string prop, float v) { foreach (var g in AssetDatabase.FindAssets("t:Material " + match)) { var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)); if (m != null && m.HasProperty(prop)) { restore.Add((m, prop, m.GetFloat(prop))); m.SetFloat(prop, v); } } }
    public static void Undo() { foreach (var r in restore) r.Item1.SetFloat(r.Item2, r.Item3); restore.Clear(); }
    public static void Off(string n)
    {
        if (n == "nonormal") { SetAll("boulder", "_NormalScale", 0f); return; }
        if (n == "noao") { SetAll("boulder", "_AORemapMin", 1f); return; }
        if (n == "smooth0") { SetAll("boulder", "_SmoothnessRemapMax", 0.05f); return; }
        var vol = Object.FindFirstObjectByType<UnityEngine.Rendering.Volume>();
        if (n == "ssao" && vol.sharedProfile.TryGet<UnityEngine.Rendering.HighDefinition.ScreenSpaceAmbientOcclusion>(out var a)) { a.active = false; return; }
        if (n == "ssgi" && vol.sharedProfile.TryGet<UnityEngine.Rendering.HighDefinition.GlobalIllumination>(out var g)) { g.active = false; return; }
        if (n == "ssr" && vol.sharedProfile.TryGet<UnityEngine.Rendering.HighDefinition.ScreenSpaceReflection>(out var s)) { s.active = false; return; }
        if (n == "shadows") { foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)) l.shadows = LightShadows.None; return; }
        Off2(n);
    }
    public static void Off2(string n) { foreach (var g in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (g.name == n && g.GetComponent<Light>() == null) g.gameObject.SetActive(false); foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (l.name == n) l.enabled = false; }
    public static void Test() { EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); foreach (var n in System.IO.File.ReadAllText("Shots/_off.txt").Split(',')) Off(n.Trim()); HdrpShot.Run(false); Undo(); EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); }
    public static void Cams()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string comps = ""; foreach (var mb in c.GetComponents<Component>()) comps += (mb == null ? "null" : mb.GetType().Name + (mb is Behaviour b ? (b.enabled ? "+" : "-") : "")) + ",";
            Debug.Log("HDRPC " + c.name + " tag=" + c.tag + " act=" + c.gameObject.activeInHierarchy + " en=" + c.enabled + " depth=" + c.depth + " pos=" + c.transform.position + " fov=" + c.fieldOfView + " comps=" + comps);
        }
    }
    public static void Ray()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var o = new Vector3(0.5f, 1.7f, 10.5f); var d = (new Vector3(0.1f, 3.0f, 27f) - o).normalized; var ray = new Ray(o, d);
        var hits = new System.Collections.Generic.List<(float, string)>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (r.enabled && r.bounds.IntersectRay(ray, out float t)) hits.Add((t, r.name + " mat=" + (r.sharedMaterial ? r.sharedMaterial.name : "-") + " sz=" + r.bounds.size));
        hits.Sort((a, b) => a.Item1.CompareTo(b.Item1)); foreach (var h in hits) Debug.Log("HDRPR " + h.Item1.ToString("F1") + " " + h.Item2);
    }
    public static void FixCam()
    {
        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); var cam = Camera.main;
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(cam.gameObject);
        foreach (var mb in cam.GetComponents<MonoBehaviour>()) if (mb != null && mb.GetType().Name == "CameraRig") mb.enabled = true;
        EditorSceneManager.SaveScene(sc); Debug.Log("HDRPC fixed");
    }
    public static void ExportTex()
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>("Assets/HDRP/Mats/rock_boulder_C_LOD0_wet_black_basalt_2048.mat");
        foreach (var k in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap" })
        {
            var src = m.GetTexture(k); if (src == null) continue;
            var rt = RenderTexture.GetTemporary(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); Graphics.Blit(src, rt); var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(1024, 1024, TextureFormat.RGB24, false); t.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); t.Apply(); RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            System.IO.File.WriteAllBytes("Shots/t_tex" + k + ".png", t.EncodeToPNG()); Debug.Log("HDRPT " + k + " " + src.name + " " + src.width + "x" + src.height + " fmt=" + (src as Texture2D)?.format);
        }
    }
    public static void Door()
    {
        if (EditorSceneManager.GetActiveScene().path != "Assets/Scenes/HadesSlice.unity") EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials) if (m != null && (m.HasProperty("_EmissiveColor") && m.GetColor("_EmissiveColor").maxColorComponent > 0.01f || m.name.Contains("Door") || m.name.Contains("Glow") || r.name.Contains("Door")))
                Debug.Log("HDRPD " + r.name + " act=" + r.gameObject.activeInHierarchy + " mat=" + m.name + " shader=" + m.shader.name + " emis=" + (m.HasProperty("_EmissiveColor") ? m.GetColor("_EmissiveColor").ToString() : "-") + " ei=" + (m.HasProperty("_EmissiveIntensity") ? m.GetFloat("_EmissiveIntensity").ToString() : "-") + " base=" + (m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : m.HasProperty("_UnlitColor") ? m.GetColor("_UnlitColor").ToString() : "-") + " pos=" + r.transform.position + " path=" + AssetDatabase.GetAssetPath(m));
        foreach (var v in Object.FindObjectsByType<UnityEngine.Rendering.HighDefinition.LocalVolumetricFog>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Debug.Log("HDRPD fog " + v.name + " pos=" + v.transform.position + " size=" + v.parameters.size + " mfp=" + v.parameters.meanFreePath + " alb=" + v.parameters.albedo);
    }
    public static void Run()
    {
        if (EditorSceneManager.GetActiveScene().path != "Assets/Scenes/HadesSlice.unity") EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Debug.Log("HDRPL " + l.name + " type=" + l.type + " en=" + l.enabled + " act=" + l.gameObject.activeInHierarchy + " int=" + l.intensity + " unit=" + l.lightUnit + " rng=" + l.range + " ang=" + l.spotAngle + " shadows=" + l.shadows + " cookie=" + (l.cookie != null ? l.cookie.name : "-") + " pos=" + l.transform.position + " rot=" + l.transform.eulerAngles + " col=" + l.color);
    }
}
