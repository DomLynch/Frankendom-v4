using System.Collections.Generic;
using UnityEngine;

// KIT GEAR v79 + LANE GEAR2 v79-80: gear that is loaded at RUNTIME from a .glb (glTFast) arrives with glTFast's shader-graph materials. The project avoids those in HDRP (Editor/HdrpGltfMats replaces every glb material in the
// scene by HDRP/Lit), and the player has never loaded a glb at runtime before, so a pink / wrong-looking piece is the most likely first problem. Two steps, both data (gear.json materials{} and items[].materials{}):
//  1. SWAP   a glTFast / error-shader material becomes HDRP/Lit: base colour map + factor and normal map are copied, metallic / smoothness / normalScale are the gear.json "materials" constants (neutral now: 0 / 0.3 / 1;
//            the glb's metallic-roughness map is NOT converted: no readable-texture blit). If the shader is not in the build the shaderFallbacks list is tried, so nothing renders pink.
//  2. ROWS   per material name (GearTable.RowFor: item[name] > materials.byName[name] > item["*"] > byName["*"]) smoothness / metallic / normalScale / sheen are applied AFTER the swap. A material that is already
//            HDRP/Lit (a wearer layer mesh, an assetdb prefab) is never edited in place: it is cloned first, the clone is returned in the list so the owner destroys it.
//            sheen > 0 switches to materials.sheenShader (the CharFabric shader graph of hdrp_char_mats.json) when that shader exists and feeds _SheenColor; without it the material stays HDRP/Lit (HDRP/Lit has no sheen).
// Off (convertGltf false) keeps the glTFast materials but still applies rows to the clones. Shader missing or no glTF material = nothing changes. Used for glb gear pieces (GearSlots.LoadPiece) and for the wearer's
// Layer_* meshes (GearSlots.StyleLayer: Steel / Leather / Heraldry).
public static class GearMaterials
{
    public static List<Material> Convert(List<Renderer> renderers, GearTable.Item item = null)
    {
        var made = new List<Material>(); var C = GearTable.Get().materials;
        if (renderers == null) return made;
        Shader sh = C.convertGltf ? FindShader(C) : null;
        var map = new Dictionary<Material, Material>();
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var mats = r.sharedMaterials; bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue;
                Material nm; if (!map.TryGetValue(m, out nm)) { nm = Resolve(m, sh, C, item, made); map[m] = nm; }
                if (nm != m) { mats[i] = nm; changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
            foreach (var m in mats) Check(m, item);
        }
        if (made.Count > 0) GearTable.Log("gear materials" + (item != null ? " of " + item.id : "") + ": " + made.Count + " material(s) made/styled");
        return made;
    }

    // "Tabard_gear", "Steel (Instance)", "Steel (Clone)" -> "Tabard", "Steel"
    public static string BaseName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int p = n.IndexOf(" (", System.StringComparison.Ordinal); if (p > 0) n = n.Substring(0, p);
        if (n.EndsWith("_gear", System.StringComparison.Ordinal)) n = n.Substring(0, n.Length - 5);
        return n;
    }

    static Shader FindShader(GearTable.MaterialCfg C)
    {
        Shader sh = string.IsNullOrEmpty(C.shader) ? null : Shader.Find(C.shader);
        for (int i = 0; sh == null && C.shaderFallbacks != null && i < C.shaderFallbacks.Length; i++)
        {
            if (string.IsNullOrEmpty(C.shaderFallbacks[i])) continue;
            sh = Shader.Find(C.shaderFallbacks[i]);
            if (sh != null) GearTable.Warn("gear materials: shader '" + C.shader + "' not found, using fallback '" + C.shaderFallbacks[i] + "'");
        }
        if (sh == null) GearTable.Warn("gear materials: shader '" + C.shader + "' (and the fallbacks) not found, the glTFast materials stay");
        return sh;
    }

    static Material Resolve(Material m, Shader sh, GearTable.MaterialCfg C, GearTable.Item item, List<Material> made)
    {
        string bn = BaseName(m.name);
        var row = GearTable.RowFor(item, bn);
        Material res = m; bool owned = false;
        if (sh != null && NeedsConvert(m, C)) { res = Make(m, sh, C); owned = true; }
        else if (row.Any) { res = Object.Instantiate(m); res.name = bn + "_gear"; owned = true; }   // never edit a shared / asset material in place
        if (row.Any) Apply(res, row, C);
        if (owned) made.Add(res);
        GearTable.Log("gear material '" + bn + "' (" + (m.shader != null ? m.shader.name : "no shader") + ")" + (owned ? " -> " + (res.shader != null ? res.shader.name : "no shader") : " kept") + (row.Any ? " row smooth=" + row.smoothness.ToString("0.##") + " metal=" + row.metallic.ToString("0.##") + " normal=" + row.normalScale.ToString("0.##") + " sheen=" + row.sheen.ToString("0.##") : " (no row)"));
        return res;
    }

    static bool NeedsConvert(Material m, GearTable.MaterialCfg C)
    {
        if (m == null) return false;
        string sn = m.shader != null ? m.shader.name : "";
        if (sn.Length == 0) return true;
        for (int i = 0; i < C.matchShaderContains.Length; i++) if (sn.IndexOf(C.matchShaderContains[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static Material Make(Material src, Shader sh, GearTable.MaterialCfg C)
    {
        var nm = new Material(sh) { name = src.name + "_gear" };
        Texture bt = FirstTexture(src, C.baseColorTextures); if (bt != null) nm.SetTexture("_BaseColorMap", bt);
        nm.SetColor("_BaseColor", FirstColor(src, C.baseColorProps));
        Texture nt = FirstTexture(src, C.normalTextures);
        if (nt != null && nm.HasProperty("_NormalMap")) { nm.SetTexture("_NormalMap", nt); nm.EnableKeyword("_NORMALMAP"); nm.EnableKeyword("_NORMALMAP_TANGENT_SPACE"); nm.SetFloat("_NormalScale", C.normalScale); }
        SetSmooth(nm, C.smoothness); SetMetal(nm, C.metallic);
        if (C.doubleSided)
        {
            Set(nm, "_CullMode", 0f); Set(nm, "_CullModeForward", 0f);
            if (nm.HasProperty("_DoubleSidedEnable")) { nm.SetFloat("_DoubleSidedEnable", 1f); nm.EnableKeyword("_DOUBLESIDED_ON"); }   // flips the back-face normal (HDRP/Lit)
        }
        return nm;
    }

    // step 2: the per-material row. -1 = leave.
    static void Apply(Material m, GearTable.MatRow row, GearTable.MaterialCfg C)
    {
        if (row.smoothness >= 0f) SetSmooth(m, row.smoothness);
        if (row.metallic >= 0f) SetMetal(m, row.metallic);
        if (row.normalScale >= 0f) Set(m, "_NormalScale", row.normalScale);
        if (row.sheen > 0f) ApplySheen(m, row.sheen, C);
    }

    static void ApplySheen(Material m, float k, GearTable.MaterialCfg C)
    {
        Shader fab = string.IsNullOrEmpty(C.sheenShader) ? null : Shader.Find(C.sheenShader);
        if (fab != null && m.shader != fab) m.shader = fab;   // same-named properties (base map, normal, mask) keep their values
        if (m.HasProperty("_SheenColor")) { var c = C.sheenColor; m.SetColor("_SheenColor", new Color(c[0] * k, c[1] * k, c[2] * k, 1f)); }
        else GearTable.Log("gear materials: sheen " + k.ToString("0.##") + " on '" + BaseName(m.name) + "' but no sheen shader ('" + C.sheenShader + "' not found): stays " + m.shader.name + " without sheen");
    }

    // HDRP/Lit: with a mask map smoothness / metallic come from its channels through the remap range, without one from _Smoothness / _Metallic. glTFast shader graph: roughnessFactor / metallicFactor.
    static void SetSmooth(Material m, float v)
    {
        v = Mathf.Clamp01(v); Set(m, "_Smoothness", v);
        if (HasMask(m) && m.HasProperty("_SmoothnessRemapMax")) { m.SetFloat("_SmoothnessRemapMax", v); if (m.HasProperty("_SmoothnessRemapMin")) m.SetFloat("_SmoothnessRemapMin", Mathf.Min(m.GetFloat("_SmoothnessRemapMin"), v)); }
        Set(m, "roughnessFactor", 1f - v);
    }
    static void SetMetal(Material m, float v)
    {
        v = Mathf.Clamp01(v); Set(m, "_Metallic", v);
        if (HasMask(m) && m.HasProperty("_MetallicRemapMax")) { m.SetFloat("_MetallicRemapMax", v); if (m.HasProperty("_MetallicRemapMin")) m.SetFloat("_MetallicRemapMin", Mathf.Min(m.GetFloat("_MetallicRemapMin"), v)); }
        Set(m, "metallicFactor", v);
    }
    static bool HasMask(Material m) { return m.HasProperty("_MaskMap") && m.GetTexture("_MaskMap") != null; }
    static void Set(Material m, string prop, float v) { if (m.HasProperty(prop)) m.SetFloat(prop, v); }

    // anything that still has no working shader renders pink: say so once, with the material and item names (the fix is a gear.json shader / shaderFallbacks entry)
    static void Check(Material m, GearTable.Item item)
    {
        if (m == null) return;
        if (m.shader == null || m.shader.name.IndexOf("InternalError", System.StringComparison.OrdinalIgnoreCase) >= 0)
            GearTable.Warn("gear material '" + m.name + "'" + (item != null ? " of " + item.id : "") + " has no working shader (renders pink): check gear.json materials.shader / shaderFallbacks");
    }

    static Texture FirstTexture(Material m, string[] props)
    {
        for (int i = 0; i < props.Length; i++) if (m.HasProperty(props[i])) { var t = m.GetTexture(props[i]); if (t != null) return t; }
        return null;
    }
    static Color FirstColor(Material m, string[] props)
    {
        for (int i = 0; i < props.Length; i++) if (m.HasProperty(props[i])) return m.GetColor(props[i]);
        return Color.white;
    }
}
