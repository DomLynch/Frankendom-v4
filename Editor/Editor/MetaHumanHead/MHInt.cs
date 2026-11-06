#if UNITY_EDITOR
// Integration render + pixel check. Batch: -executeMethod MHInt.RenderCheck -mhname int5 -mhcanon <third_party> [-mhref <UE png>]
// Rebuilds from canonical (MHRebuildAll), renders the 1024 studio neutral, eyes crop and side-by-side with the UE reference, then compares iris / nose / cheek / forehead
// boxes (look.qa) against the UE image. Prints "[MHINT] PASS ..." / "[MHINT] FAIL ..." lines, writes TestOut\int_check_<name>.txt, exits 3 on any FAIL.
using System.IO; using System.Text; using UnityEditor; using UnityEditor.SceneManagement; using UnityEngine; using MetaHumanRig;

public static class MHInt
{
    static string Arg(string n, string d) { var a = System.Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == n) return a[i + 1]; return d; }
    static void Save(Camera cam, int N, string path)
    {
        var rt = new RenderTexture(N, N, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); cam.targetTexture = rt; for (int i = 0; i < 4; i++) cam.Render();
        RenderTexture.active = rt; var png = new Texture2D(N, N, TextureFormat.RGB24, false); png.ReadPixels(new Rect(0, 0, N, N), 0, 0); png.Apply();
        File.WriteAllBytes(path, png.EncodeToPNG()); RenderTexture.active = null; cam.targetTexture = null; rt.Release();
    }
    static Texture2D Load1024(string path)
    {
        var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path)); var rt = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(t, rt); RenderTexture.active = rt; var o = new Texture2D(1024, 1024, TextureFormat.RGB24, false); o.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); o.Apply(); RenderTexture.active = null; rt.Release(); return o;
    }
    // mean RGB (0-255) over box [x0,y0,x1,y1] (y from top) of the darkest `frac` pixels by luma (frac 1 = all)
    static Vector3 Mean(Texture2D t, int[] b, float frac)
    {
        int n = (b[2] - b[0]) * (b[3] - b[1]); var px = new Color32[n]; var lum = new float[n]; int k = 0;
        for (int y = b[1]; y < b[3]; y++) for (int x = b[0]; x < b[2]; x++) { px[k] = t.GetPixel(x, 1023 - y); lum[k] = 0.299f * px[k].r + 0.587f * px[k].g + 0.114f * px[k].b; k++; }
        System.Array.Sort(lum, px); int m = Mathf.Max(1, (int)(n * frac)); Vector3 s = Vector3.zero; for (int i = 0; i < m; i++) s += new Vector3(px[i].r, px[i].g, px[i].b); return s / m;
    }
    static float HistL1(Texture2D a, Texture2D b, int[] box)
    {
        var ha = new float[48]; var hb = new float[48]; int n = (box[2] - box[0]) * (box[3] - box[1]);
        for (int y = box[1]; y < box[3]; y++) for (int x = box[0]; x < box[2]; x++) { Color32 p = a.GetPixel(x, 1023 - y), q = b.GetPixel(x, 1023 - y); ha[p.r / 16]++; ha[16 + p.g / 16]++; ha[32 + p.b / 16]++; hb[q.r / 16]++; hb[16 + q.g / 16]++; hb[32 + q.b / 16]++; }
        float d = 0; for (int i = 0; i < 48; i++) d += Mathf.Abs(ha[i] - hb[i]) / n; return d;
    }
    // std-dev of the 4-neighbour Laplacian of luma inside box (pore / crease high-frequency energy)
    static float Hf(Texture2D t, int[] box)
    {
        int w = box[2] - box[0], h = box[3] - box[1]; var g = new float[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { Color32 p = t.GetPixel(box[0] + x, 1023 - (box[1] + y)); g[y * w + x] = 0.299f * p.r + 0.587f * p.g + 0.114f * p.b; }
        double s = 0, s2 = 0; int n = 0; for (int y = 1; y < h - 1; y++) for (int x = 1; x < w - 1; x++) { float l = 4 * g[y * w + x] - g[(y - 1) * w + x] - g[(y + 1) * w + x] - g[y * w + x - 1] - g[y * w + x + 1]; s += l; s2 += l * l; n++; }
        double mean = s / n; return (float)System.Math.Sqrt(s2 / n - mean * mean);
    }
    static float Luma(Vector3 c) { return 0.299f * c.x + 0.587f * c.y + 0.114f * c.z; }

    public static void RenderCheck()
    {
        string name = Arg("-mhname", "int"); Directory.CreateDirectory("TestOut"); var rep = new StringBuilder(); bool fail = false;
        MHRebuildAll.RebuildAllFromCanonical();
        EditorSceneManager.OpenScene("Assets/Scenes/MHTest.unity");
        var b = Object.FindAnyObjectByType<MHHeadBuilder>(); var cam = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        b.headMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MetaHumanHead/HDRP/MH_Skin_HDRP.mat"); b.teethMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MetaHumanHead/HDRP/MH_HDRP_teeth.mat");
        if (b.headMaterial == null) { rep.AppendLine("[MHINT] FAIL skin material asset missing"); fail = true; }
        b.lod = 0; b.Build(); b.EvaluateNow(); b.EvaluateNow();
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.transform.parent == null) l.gameObject.SetActive(false);
        b.ApplyStudioRig(cam);
        string neutral = "TestOut/" + name + ".png"; Save(cam, 1024, neutral);
        Renderer le = null, re = null; foreach (var r in b.GetComponentsInChildren<Renderer>()) { if (r.name.StartsWith("eyeLeft_")) le = r; if (r.name.StartsWith("eyeRight_")) re = r; }
        var c = (le.bounds.center + re.bounds.center) * 0.5f; cam.transform.position = c - cam.transform.forward * 0.3f; Save(cam, 1024, "TestOut/eyes_crop_" + name + ".png");
        string refp = Arg("-mhref", @"C:\Users\dom12\Documents\kits_out\3d\metahuman\renders\AGE_F41b_F41_front.png");
        string e; var look = MHLook.Load(MHIntegrate.LookAsset(), out e); var Q = look.qa;
        var me = Load1024(neutral); var ue = File.Exists(refp) ? Load1024(refp) : null;
        if (ue != null)
        {
            var sbs = new Texture2D(2048, 1024, TextureFormat.RGB24, false); sbs.SetPixels(0, 0, 1024, 1024, ue.GetPixels()); sbs.SetPixels(1024, 0, 1024, 1024, me.GetPixels()); sbs.Apply(); File.WriteAllBytes("TestOut/sbs_" + name + ".png", sbs.EncodeToPNG());
        }
        for (int s = 0; s < 2; s++)
        {
            var m = Mean(me, s == 0 ? Q.irisBoxL : Q.irisBoxR, Q.irisDarkFraction); float lum = Luma(m), rb = m.x / Mathf.Max(m.z, 1f);
            bool ok = lum <= Q.irisLumaMax && rb >= Q.irisRedOverBlueMin; fail |= !ok;
            rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " iris" + (s == 0 ? "L" : "R") + " rgb=" + m.ToString("F0") + " luma=" + lum.ToString("F0") + " (max " + Q.irisLumaMax + ") R/B=" + rb.ToString("F2") + " (min " + Q.irisRedOverBlueMin + ")");
        }
        var names = new[] { "nose", "cheek", "forehead" }; var boxes = new[] { Q.noseBox, Q.cheekBox, Q.foreheadBox };
        for (int i = 0; i < 3; i++)
        {
            var m = Mean(me, boxes[i], 1f); string txt = ""; float gr = m.y / Mathf.Max(m.x, 1f); bool ok = gr >= Q.gOverRMin && gr <= Q.gOverRMax;
            if (ue != null) { var u = Mean(ue, boxes[i], 1f); float dl = 100f * (Luma(m) - Luma(u)) / Mathf.Max(Luma(u), 1f); ok &= Mathf.Abs(dl) <= Q.regionLumaTolPct; txt = " UE=" + u.ToString("F0") + " dLuma=" + dl.ToString("F1") + "% (tol " + Q.regionLumaTolPct + ")"; }
            fail |= !ok; rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " " + names[i] + " rgb=" + m.ToString("F0") + " G/R=" + gr.ToString("F2") + " (" + Q.gOverRMin + ".." + Q.gOverRMax + ")" + txt);
        }
        { var bm = Mean(me, Q.backdropBox, 1f); var ex = look.studio.backdropSrgb; float dmax = Mathf.Max(Mathf.Abs(bm.x - ex[0]), Mathf.Abs(bm.y - ex[1]), Mathf.Abs(bm.z - ex[2])); bool ok = dmax <= Q.backdropTolerance; fail |= !ok;
          rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " backdrop rgb=" + bm.ToString("F0") + " expected=" + ex[0] + "," + ex[1] + "," + ex[2] + " maxDelta=" + dmax.ToString("F0") + " (tol " + Q.backdropTolerance + ")" + (ok ? "" : " -> studio rig NOT applied? (MH_HDRP define missing)")); }
        // whole-disc iris mean (all pixels in the circle)
        for (int s2 = 0; s2 < 2; s2++)
        {
            var cc = s2 == 0 ? Q.irisCentreL : Q.irisCentreR; Vector3 sum = Vector3.zero; int cnt = 0; int rr = Q.irisDiscRadius;
            for (int y = -rr; y <= rr; y++) for (int x = -rr; x <= rr; x++) if (x * x + y * y <= rr * rr) { Color32 px = me.GetPixel(cc[0] + x, 1023 - (cc[1] + y)); sum += new Vector3(px.r, px.g, px.b); cnt++; }
            var m = sum / cnt; float lum = Luma(m), rb = m.x / Mathf.Max(m.z, 1f); bool ok = lum <= Q.irisDiscLumaMax && rb >= Q.irisDiscRedOverBlueMin && m.x >= Q.irisDiscRMin; fail |= !ok;
            string ut = ""; if (ue != null) { Vector3 us = Vector3.zero; int uc = 0; for (int y = -rr; y <= rr; y++) for (int x = -rr; x <= rr; x++) if (x * x + y * y <= rr * rr) { Color32 px = ue.GetPixel(cc[0] + x, 1023 - (cc[1] + y)); us += new Vector3(px.r, px.g, px.b); uc++; } ut = " UE=" + (us / uc).ToString("F0"); }
            rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " irisDisc" + (s2 == 0 ? "L" : "R") + " rgb=" + m.ToString("F0") + " luma=" + lum.ToString("F0") + " (max " + Q.irisDiscLumaMax + ") R/B=" + rb.ToString("F2") + " (min " + Q.irisDiscRedOverBlueMin + ")" + ut);
        }
        // brows, crown, moustache (darkest fraction of the box)
        var gn = new[] { "browL", "browR", "crown", "moustache" }; var gb = new[] { Q.browBoxL, Q.browBoxR, Q.crownBox, Q.moustacheBox };
        for (int i = 0; i < 4; i++)
        {
            var m = Mean(me, gb[i], Q.groupDarkFraction); float lum = Luma(m), gr = m.y / Mathf.Max(m.x, 1f); bool ok; string lim;
            if (i < 2) { ok = lum <= Q.browLumaMax && m.x < Q.browRMax && gr >= Q.browGOverRMin && gr <= Q.browGOverRMax; lim = "luma<=" + Q.browLumaMax + " R<" + Q.browRMax + " G/R " + Q.browGOverRMin + ".." + Q.browGOverRMax; }
            else if (i == 2) { ok = lum <= Q.crownLumaMax && (lum < 5f || (gr >= Q.crownGOverRMin && gr <= Q.crownGOverRMax)); lim = "luma<=" + Q.crownLumaMax + " G/R " + Q.crownGOverRMin + ".." + Q.crownGOverRMax; }
            else { ok = lum <= Q.moustacheLumaMax; lim = "luma<=" + Q.moustacheLumaMax; }
            string ut = ""; if (ue != null) { var u = Mean(ue, gb[i], Q.groupDarkFraction); ut = " UE=" + u.ToString("F0"); }
            fail |= !ok; rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " " + gn[i] + " rgb=" + m.ToString("F0") + " luma=" + lum.ToString("F0") + " G/R=" + gr.ToString("F2") + " (" + lim + ")" + ut);
        }
        for (int s3 = 0; s3 < 2; s3++)   // lid / brow-ridge strip (B3)
        {
            var lb = s3 == 0 ? Q.lidBoxL : Q.lidBoxR; var m = Mean(me, lb, 1f); float lum = Luma(m), rb = m.x / Mathf.Max(m.z, 1f); bool ok = rb <= Q.lidRoverBMax && lum >= Q.lidLumaMin && lum <= Q.lidLumaMax; fail |= !ok;
            rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " lidBand" + (s3 == 0 ? "L" : "R") + " rgb=" + m.ToString("F0") + " luma=" + lum.ToString("F0") + " (" + Q.lidLumaMin + ".." + Q.lidLumaMax + ") R/B=" + rb.ToString("F2") + " (max " + Q.lidRoverBMax + ")" + (ue != null ? " UE=" + Mean(ue, lb, 1f).ToString("F0") : ""));
        }
        if (ue != null)
        {
            float h = HistL1(me, ue, Q.faceBox); bool ok = h <= Q.histL1Max; fail |= !ok; rep.AppendLine("[MHINT] " + (ok ? "PASS" : "FAIL") + " faceHistogramL1=" + h.ToString("F2") + " (max " + Q.histL1Max + ")");
            float hu = Hf(ue, Q.foreheadBox), hm = Hf(me, Q.foreheadBox), ratio = hm / Mathf.Max(hu, 1e-3f); bool ok2 = ratio <= Q.foreheadHfRatioMax && ratio >= Q.foreheadHfRatioMin; fail |= !ok2;
            rep.AppendLine("[MHINT] " + (ok2 ? "PASS" : "FAIL") + " foreheadHighFreq unity=" + hm.ToString("F2") + " UE=" + hu.ToString("F2") + " ratio=" + ratio.ToString("F2") + " (" + Q.foreheadHfRatioMin + ".." + Q.foreheadHfRatioMax + ")");
        }
        rep.AppendLine("[MHINT] " + (fail ? "OVERALL FAIL " : "OVERALL PASS ") + name);
        File.WriteAllText("TestOut/int_check_" + name + ".txt", rep.ToString()); Debug.Log(rep.ToString());
        EditorApplication.Exit(fail ? 3 : 0);
    }
}
#endif
