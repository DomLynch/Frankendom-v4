using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine;

// Rebuilds the URP materials (exported to Assets/HDRP/urp_materials.json by Tools/export_mats.py) as HDRP materials: URP Lit -> HDRP/Lit, URP Particles Unlit -> HDRP/Unlit (transparent).
public static class HdrpConvert
{
    [System.Serializable] class Entry { public string path, kind; public List<string> keys; }
    public static void Run()
    {
        var json = File.ReadAllText("Assets/HDRP/urp_materials.json");
        var list = MiniJson(json);
        int lit = 0, part = 0;
        var hdLit = Shader.Find("HDRP/Lit"); var hdUnlit = Shader.Find("HDRP/Unlit");
        foreach (var e in list)
        {
            string path = (string)e["path"]; string kind = (string)e["kind"];
            var m = AssetDatabase.LoadAssetAtPath<Material>(path); if (m == null) continue;
            var tex = (Dictionary<string, object>)e["tex"]; var f = (Dictionary<string, object>)e["f"]; var c = (Dictionary<string, object>)e["c"];
            Texture T(string k) { if (!tex.ContainsKey(k)) return null; var parts = ((string)tex[k]).Split(new[] { (char)58 }); string tp = AssetDatabase.GUIDToAssetPath(parts[0]); if (parts.Length < 2 || !tp.EndsWith(".glb")) return AssetDatabase.LoadAssetAtPath<Texture>(tp); long want = long.Parse(parts[1]); foreach (var o in AssetDatabase.LoadAllAssetsAtPath(tp)) { if (!(o is Texture tx)) continue; if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string g2, out long lid) && lid == want) return tx; } return AssetDatabase.LoadAssetAtPath<Texture>(tp); }
            float F(string k, float d) { return f.ContainsKey(k) ? System.Convert.ToSingle(f[k]) : d; }
            Color C(string k, Color d) { if (!c.ContainsKey(k)) return d; var a = (List<object>)c[k]; return new Color(System.Convert.ToSingle(a[0]), System.Convert.ToSingle(a[1]), System.Convert.ToSingle(a[2]), System.Convert.ToSingle(a[3])); }
            if (kind == "lit" || tex.ContainsKey("_BaseMap") || tex.ContainsKey("_BaseColorMap"))
            {
                var bm = T("_BaseColorMap") ?? T("_BaseMap"); var bc = C("_BaseColor", Color.white); var nm = T("_NormalMap") ?? T("_BumpMap"); var em = T("_EmissiveColorMap") ?? T("_EmissionMap"); var ec = C("_EmissionColor", Color.black);
                float met = F("_Metallic", 0f), sm = F("_Smoothness", 0.5f); bool transparent = F("_Surface", 0f) > 0.5f || path.Contains("M_Water");
                m.shader = hdLit;
                m.SetColor("_BaseColor", bc); m.SetTexture("_BaseColorMap", bm); if (nm) { m.SetTexture("_NormalMap", nm); m.SetFloat("_NormalScale", F("_BumpScale", 1f)); }
                m.SetFloat("_Metallic", met); m.SetFloat("_Smoothness", path.Contains("M_Water") ? 0.95f : sm);
                if (em != null || ec.maxColorComponent > 0.001f) { m.SetColor("_EmissiveColor", new Color(ec.r, ec.g, ec.b)); m.SetTexture("_EmissiveColorMap", em); }
                if (transparent) { m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 0f); }
                var tiling = new Vector2(1, 1);
                HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); lit++;
            }
            else if (kind == "other" && tex.ContainsKey("baseColorTexture"))
            {
                // glTFast URP shader-graph copies (matte_*): rebuild as HDRP/Lit, matte
                m.shader = hdLit; m.SetTexture("_BaseColorMap", T("baseColorTexture")); m.SetColor("_BaseColor", Color.white);
                if (tex.ContainsKey("normalTexture")) m.SetTexture("_NormalMap", T("normalTexture"));
                m.SetFloat("_Metallic", 0f); m.SetFloat("_Smoothness", 0.2f);
                HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); lit++;
            }
            else if (kind == "other" && (path.Contains("M_GroundFog") || path.Contains("M_Ridge")))
            {
                bool fogq = path.Contains("M_GroundFog"); var bc = C("_BaseColor", Color.white);
                m.shader = fogq ? hdUnlit : hdLit;
                if (fogq) { m.SetColor("_UnlitColor", bc); m.SetTexture("_UnlitColorMap", T("_BaseMap")); m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 0f); m.SetFloat("_ZWrite", 0f); }
                else { m.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.06f)); m.SetFloat("_Smoothness", 0.1f); }
                HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); part++;
            }            else if (kind == "particle")
            {
                var bm = T("_BaseMap"); var bc = C("_BaseColor", Color.white);
                bool additive = F("_DstBlend", 10f) == 1f;
                m.shader = hdUnlit;
                m.SetColor("_UnlitColor", bc); m.SetTexture("_UnlitColorMap", bm);
                m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", additive ? 1f : 0f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_EnableFogOnTransparent", 1f);
                m.SetColor("_EmissiveColor", Color.black);
                HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); part++;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"HDRPCONVERT lit={lit} particle={part}");
    }

    // tiny JSON reader (objects -> Dictionary, arrays -> List, numbers -> double, strings)
    static List<Dictionary<string, object>> MiniJson(string s)
    {
        int i = 0; var root = (List<object>)Parse(s, ref i); var res = new List<Dictionary<string, object>>(); foreach (var o in root) res.Add((Dictionary<string, object>)o); return res;
    }
    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    static object Parse(string s, ref int i)
    {
        Ws(s, ref i); char ch = s[i];
        if (ch == '{') { i++; var d = new Dictionary<string, object>(); Ws(s, ref i); if (s[i] == '}') { i++; return d; } while (true) { Ws(s, ref i); var k = (string)Parse(s, ref i); Ws(s, ref i); i++; var v = Parse(s, ref i); d[k] = v; Ws(s, ref i); if (s[i] == ',') { i++; continue; } i++; return d; } }
        if (ch == '[') { i++; var l = new List<object>(); Ws(s, ref i); if (s[i] == ']') { i++; return l; } while (true) { l.Add(Parse(s, ref i)); Ws(s, ref i); if (s[i] == ',') { i++; continue; } i++; return l; } }
        if (ch == '"') { i++; int st = i; while (s[i] != '"') { if (s[i] == '\\') i++; i++; } var r = s.Substring(st, i - st).Replace("\\\\", "\\"); i++; return r; }
        int st2 = i; while (i < s.Length && ",}] \r\n".IndexOf(s[i]) < 0) i++; var tok = s.Substring(st2, i - st2);
        if (tok == "true") return true; if (tok == "false") return false; if (tok == "null") return null; return double.Parse(tok, System.Globalization.CultureInfo.InvariantCulture);
    }
}
