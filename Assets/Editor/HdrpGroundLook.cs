using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// lane LOOK fix 3: break up the ground tiling, MATERIAL ONLY (no mesh, collider, layout or transform is touched).
// Data: look.json "ground". For each listed HDRP/Lit material:
//   - _NormalScale (absolute, -1 = leave): the repeating worm-pattern normal is toned down (the paving had 1.0; 0.6 = -40 %)
//   - an HDRP detail map at a second, coprime UV scale (detailTiling) carrying macro albedo variation (R) and roughness / smoothness variation (B); its normal channels are flat (G = A = 0.5)
//     and _DetailNormalScale is 0, so the detail adds no extra normal noise
//   - the material's mask map blue channel (= HDRP Lit detail mask; the generated masks write 0 there, which would hide the detail) is set to 1. Mask R / G / A are untouched.
// The detail texture is a tileable fBm value-noise PNG generated here (deterministic from size / cells / octaves / seed; only rewritten when its bytes change).
// Called from HdrpScene.Apply, HdrpGltfMats.Run and HdrpWorldMats.Run (after those rebuild the ground materials and masks). Idempotent.
public static class HdrpGroundLook
{
    [MenuItem("Hades/World/Apply ground breakup (look.json)")]
    public static void Run()
    {
        LookData.Reload(); var G = LookData.Get().ground;
        if (G == null || !G.enabled || G.materials == null || G.materials.Length == 0) { Debug.Log("HDRPGROUNDLOOK off"); return; }
        var detail = EnsureDetail(G); int n = 0;
        foreach (var e in G.materials)
        {
            if (e == null || string.IsNullOrEmpty(e.path)) continue;
            var m = AssetDatabase.LoadAssetAtPath<Material>(e.path);
            if (m == null) { Debug.Log("HDRPGROUNDLOOK material not found (skipped): " + e.path); continue; }
            if (e.normalScale >= 0f && m.HasProperty("_NormalScale")) m.SetFloat("_NormalScale", e.normalScale);
            if (e.smoothnessMin >= 0f && m.HasProperty("_SmoothnessRemapMin")) m.SetFloat("_SmoothnessRemapMin", e.smoothnessMin);
            if (e.smoothnessMax >= 0f && m.HasProperty("_SmoothnessRemapMax")) m.SetFloat("_SmoothnessRemapMax", e.smoothnessMax);
            if (detail != null && e.detailTiling > 0f && m.HasProperty("_DetailMap"))
            {
                m.SetTexture("_DetailMap", detail); m.SetTextureScale("_DetailMap", new Vector2(e.detailTiling, e.detailTiling));
                if (m.HasProperty("_DetailAlbedoScale")) m.SetFloat("_DetailAlbedoScale", e.detailAlbedo);
                if (m.HasProperty("_DetailNormalScale")) m.SetFloat("_DetailNormalScale", 0f);
                if (m.HasProperty("_DetailSmoothnessScale")) m.SetFloat("_DetailSmoothnessScale", e.detailSmoothness);
                if (m.HasProperty("_MaskMap")) OpenDetailMask(m.GetTexture("_MaskMap"));
            }
            HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); n++;
            Debug.Log("HDRPGROUNDLOOK " + m.name + " normal=" + (m.HasProperty("_NormalScale") ? m.GetFloat("_NormalScale").ToString("F2") : "-") + " detailTiling=" + e.detailTiling);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("HDRPGROUNDLOOK materials=" + n);
    }

    // mask blue = detail mask: force 255 (only when a pixel is below it) so the detail map shows. PNG masks only; others are reported and left.
    static void OpenDetailMask(Texture t)
    {
        if (t == null) return; string p = AssetDatabase.GetAssetPath(t);
        if (string.IsNullOrEmpty(p) || !p.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) || !File.Exists(p)) { Debug.LogWarning("HDRPGROUNDLOOK mask not a PNG, detail mask left as is: " + p); return; }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!tex.LoadImage(File.ReadAllBytes(p))) { Object.DestroyImmediate(tex); return; }
        var px = tex.GetPixels32(); bool ch = false;
        for (int i = 0; i < px.Length; i++) if (px[i].b != 255) { px[i].b = 255; ch = true; }
        if (ch) { tex.SetPixels32(px); tex.Apply(); SafeIO.WriteAllBytes(p, tex.EncodeToPNG()); /* LOCKFIX v103 */ AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceSynchronousImport); Debug.Log("HDRPGROUNDLOOK detail mask opened " + p); }
        Object.DestroyImmediate(tex);
    }

    static Texture2D EnsureDetail(LookData.GroundRow G)
    {
        if (string.IsNullOrEmpty(G.detailTexture)) return null;
        int s = Mathf.Clamp(G.size, 64, 2048), cells = Mathf.Max(1, G.cells), oct = Mathf.Clamp(G.octaves, 1, 8);
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false, true); var px = new Color32[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (float)x / s, v = (float)y / s;
                float a = Fbm(u, v, cells, oct, G.seed), r = Fbm(u, v, cells + 1, oct, G.seed + 101);   // separate fields so dark patches are not always the rough ones
                byte R = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * (0.5f + G.albedoVar * (a - 0.5f))), 0, 255);
                byte B = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * (0.5f + G.roughVar * (r - 0.5f))), 0, 255);
                px[y * s + x] = new Color32(R, 128, B, 128);
            }
        tex.SetPixels32(px); tex.Apply(); var bytes = tex.EncodeToPNG(); Object.DestroyImmediate(tex);
        string p = G.detailTexture; Directory.CreateDirectory(Path.GetDirectoryName(p));
        bool write = !File.Exists(p) || !System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes(p), bytes);
        if (write) { SafeIO.WriteAllBytes(p, bytes); /* LOCKFIX v103 */ AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceSynchronousImport); }
        var ti = AssetImporter.GetAtPath(p) as TextureImporter;
        if (ti != null && (ti.sRGBTexture || ti.wrapMode != TextureWrapMode.Repeat || ti.textureType != TextureImporterType.Default || !ti.mipmapEnabled))
        { ti.textureType = TextureImporterType.Default; ti.sRGBTexture = false; ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true; ti.textureCompression = TextureImporterCompression.CompressedHQ; ti.anisoLevel = 8; ti.SaveAndReimport(); }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
    }

    // periodic value-noise fBm in 0..1 (tiles at u, v = 0..1: every octave's lattice period divides the texture)
    static float Fbm(float u, float v, int cells, int oct, int seed)
    {
        float sum = 0f, norm = 0f, amp = 1f; int c = cells;
        for (int o = 0; o < oct; o++) { sum += amp * Value(u * c, v * c, c, seed + o * 31); norm += amp; amp *= 0.5f; c *= 2; }
        return sum / norm;
    }
    static float Value(float x, float y, int period, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float h00 = Hash(x0, y0, period, seed), h10 = Hash(x0 + 1, y0, period, seed), h01 = Hash(x0, y0 + 1, period, seed), h11 = Hash(x0 + 1, y0 + 1, period, seed);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fy);
    }
    static float Hash(int x, int y, int period, int seed)
    {
        x = ((x % period) + period) % period; y = ((y % period) + period) % period;
        unchecked { uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041); h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16; return (h & 0xFFFFFF) / 16777215f; }
    }
}
