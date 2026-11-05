using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Scene fixes driven by Assets/Data/hdrp_fixes.json: objects to hide (misplaced black cards), light layers (fill light skips the trees).
public static class HdrpFixes
{
    [System.Serializable] public class Box { public string name; public float[] pos, size, color; }
    [System.Serializable] public class MatAdj { public string match; public float[] color; public float normalScale = -1f; public float tiling; public float metallicMax = -1f, smoothnessMax = -1f, smoothnessMin = -1f, aoStrength = -1f, emissiveScale = -1f; }
    [System.Serializable] public class Water { public float x, y, z, size, metallic, smoothness; public float[] color; public string normalMap; public float normalScale = 1f, normalTiling = 1f; public float[] scroll; }
    // generic placed prop (data layer: hdrp_fixes.json "props"). glb = LOD0 model asset; lods = optional LOD1, LOD2... model assets; lodHeights = LODGroup screen-relative transition heights (one per LOD, last = cull height)
    [System.Serializable] public class Prop { public string name, glb; public string[] lods; public float[] pos, euler, scale, lodHeights; }
    // lane LOOK fix 2: roster-keyed rim rigs (who = "player" | "enemy" | roster id). Spot light, shadows off, only on that character's renderers (light layer bit), follows it from behind / above (RimRig).
    // lux = illuminance at the aim point; the light is set in candela = lux * distance^2 (distance from the offsets), colour from kelvin.
    [System.Serializable] public class RimRigRow { public string name = "", who = "player"; public int layerBit = 5; public float kelvin = 7000f, lux = 2500f, behind = 2.5f, above = 2.6f, side = 0.6f, aimHeight = 1.4f, spotAngle = 50f, range = 8f, volumetricDimmer = 0f; }
    [System.Serializable] public class Fx { public RimRigRow[] rimRigs; public Prop[] props; public Water water; public MatAdj[] matAdjust; public Box[] boxes; public string[] hide; public string fillLight; public string[] fillExclude; public int fillLayerBit; public string rimLight; public int rimLayerBit; public string[] rimInclude; public string rim2Light; public int rim2LayerBit; public string[] rim2Include; public string keepParticlesUnder; public string rim3Light; public int rim3LayerBit; public string[] rim3Include; public string[] rimExclude; }
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene(); if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var F = JsonUtility.FromJson<Fx>(System.IO.File.ReadAllText("Assets/Data/hdrp_fixes.json"));
        foreach (var n in F.hide) { foreach (var t in Resources.FindObjectsOfTypeAll<Transform>()) if (t.gameObject.scene.IsValid() && t.name == n) t.gameObject.SetActive(false); }
        BuildProps(scene, F.props);
        BuildClashSheets(scene);
        uint bit = 1u << F.fillLayerBit; int hit = 0, skip = 0;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            string path = r.transform.root.name + "/" + AnimationUtility_GetPath(r.transform);
            bool excl = false; foreach (var e in F.fillExclude) if (path.Contains(e)) excl = true;
            if (excl) { r.renderingLayerMask &= ~bit; skip++; } else { r.renderingLayerMask |= bit; hit++; }
        }
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.name == F.fillLight) { var hd = l.GetComponent<HDAdditionalLightData>(); if (hd != null) hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)bit; }
        // the URP-era particle systems (smoke banks, haze, ash, door fire...) render as huge faceted white polygons in HDRP: HDRP volumetric fog + the furnace replace them
        if (!string.IsNullOrEmpty(F.keepParticlesUnder)) { var keep = GameObject.Find(F.keepParticlesUnder); int off = 0; foreach (var ps in Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (keep == null || !ps.transform.IsChildOf(keep.transform)) { ps.gameObject.SetActive(false); off++; } Debug.Log("HDRPFIXES old particle systems disabled=" + off); }
        // opaque blockers (stopgap chamber shell: keeps the HDRI sky from showing through the doorway)
        if (F.boxes == null || F.boxes.Length == 0) { var stale = GameObject.Find("DoorChamberShell"); if (stale != null) Object.DestroyImmediate(stale); }
        if (F.boxes != null) foreach (var b in F.boxes)
        {
            var old = GameObject.Find(b.name); if (old != null) Object.DestroyImmediate(old);
            var bx = GameObject.CreatePrimitive(PrimitiveType.Cube); bx.name = b.name; bx.transform.position = new Vector3(b.pos[0], b.pos[1], b.pos[2]); bx.transform.localScale = new Vector3(b.size[0], b.size[1], b.size[2]);
            Object.DestroyImmediate(bx.GetComponent<Collider>());
            string mp = "Assets/HDRP/Mats/" + b.name + ".mat"; var bm = AssetDatabase.LoadAssetAtPath<Material>(mp); if (bm == null) { bm = new Material(Shader.Find("HDRP/Unlit")); AssetDatabase.CreateAsset(bm, mp); }
            bm.shader = Shader.Find("HDRP/Unlit"); bm.SetColor("_UnlitColor", new Color(b.color[0], b.color[1], b.color[2], 1f)); HDMaterial.ValidateMaterial(bm); EditorUtility.SetDirty(bm);
            bx.GetComponent<Renderer>().sharedMaterial = bm; bx.GetComponent<Renderer>().renderingLayerMask = 1;
        }
        // character-only rim light: its huge cone lit the walkway floor as a pale wedge, so it only affects the fighters' renderers
        var rimSets = new[] { (F.rimLight, F.rimLayerBit, F.rimInclude), (F.rim2Light, F.rim2LayerBit, F.rim2Include), (F.rim3Light, F.rim3LayerBit, F.rim3Include) };
        foreach (var rs in rimSets)
        {
            if (string.IsNullOrEmpty(rs.Item1)) continue;
            uint rb = 1u << rs.Item2; int rim = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                string path = r.transform.root.name + "/" + AnimationUtility_GetPath(r.transform); bool inc = false; foreach (var e in rs.Item3) if (path.Contains(e)) inc = true; if (inc && F.rimExclude != null) foreach (var x in F.rimExclude) if (!string.IsNullOrEmpty(x) && path.Contains(x)) inc = false;
                if (inc) { r.renderingLayerMask |= rb; rim++; } else r.renderingLayerMask &= ~rb;
            }
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.name == rs.Item1) { var hd = l.GetComponent<HDAdditionalLightData>(); if (hd != null) hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)rb; }
            Debug.Log("HDRPFIXES rim " + rs.Item1 + " renderers=" + rim);
        }
        BuildRimRigs(F);
        // dark water surface under the walkway: faint cold moonlit sheen so the waterline reads in the shadows (data: water block in hdrp_fixes.json)
        { var old = GameObject.Find("WaterSheen"); if (old != null) Object.DestroyImmediate(old); }
        if (F.water != null && F.water.size > 0f)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Plane); w.name = "WaterSheen"; w.transform.position = new Vector3(F.water.x, F.water.y, F.water.z); w.transform.localScale = Vector3.one * (F.water.size / 10f);
            Object.DestroyImmediate(w.GetComponent<Collider>());
            string wp = "Assets/HDRP/Mats/WaterSheen.mat"; var wm = AssetDatabase.LoadAssetAtPath<Material>(wp); if (wm == null) { wm = new Material(Shader.Find("HDRP/Lit")); AssetDatabase.CreateAsset(wm, wp); }
            wm.SetColor("_BaseColor", new Color(F.water.color[0], F.water.color[1], F.water.color[2], 1f)); wm.SetFloat("_Metallic", F.water.metallic); wm.SetFloat("_Smoothness", F.water.smoothness);
            if (!string.IsNullOrEmpty(F.water.normalMap) && System.IO.File.Exists(F.water.normalMap)) { var ti = AssetImporter.GetAtPath(F.water.normalMap) as TextureImporter; if (ti != null && (ti.textureType != TextureImporterType.NormalMap || ti.wrapMode != TextureWrapMode.Repeat)) { ti.textureType = TextureImporterType.NormalMap; ti.wrapMode = TextureWrapMode.Repeat; ti.SaveAndReimport(); } var nt = AssetDatabase.LoadAssetAtPath<Texture2D>(F.water.normalMap); if (nt != null) { wm.SetTexture("_NormalMap", nt); wm.EnableKeyword("_NORMALMAP"); wm.SetFloat("_NormalScale", F.water.normalScale); wm.SetTextureScale("_NormalMap", new Vector2(F.water.normalTiling, F.water.normalTiling)); } }
            HDMaterial.ValidateMaterial(wm); EditorUtility.SetDirty(wm);
            var wr = w.GetComponent<Renderer>(); wr.sharedMaterial = wm; wr.renderingLayerMask = 1; wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (F.water.scroll != null && F.water.scroll.Length >= 2) { var su = w.AddComponent<ScrollUV>(); su.speed = new Vector2(F.water.scroll[0], F.water.scroll[1]); }
        }
        // absolute material overrides by name substring (idempotent): colour, float props, texture tiling
        if (F.matAdjust != null) foreach (var a in F.matAdjust)
            foreach (var g in AssetDatabase.FindAssets("t:Material " + a.match))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)); if (m == null || !m.name.Contains(a.match)) continue;
                if (a.color != null && a.color.Length >= 3 && m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(a.color[0], a.color[1], a.color[2], 1f));
                if (a.normalScale >= 0f && m.HasProperty("_NormalScale")) m.SetFloat("_NormalScale", a.normalScale);
                if (a.tiling > 0f) foreach (var tp in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap", "_HeightMap" }) if (m.HasProperty(tp)) m.SetTextureScale(tp, new Vector2(a.tiling, a.tiling));
                // HDRP/Lit with a mask map: metallic = mask.r and smoothness = mask.a, remapped between the Min/Max props, so the caps go on the remap Max (blown-out polished metal)
                if (a.metallicMax >= 0f && m.HasProperty("_MetallicRemapMax")) m.SetFloat("_MetallicRemapMax", a.metallicMax);
                if (a.smoothnessMax >= 0f && m.HasProperty("_SmoothnessRemapMax")) m.SetFloat("_SmoothnessRemapMax", a.smoothnessMax);
                if (a.smoothnessMin >= 0f && m.HasProperty("_SmoothnessRemapMin")) m.SetFloat("_SmoothnessRemapMin", a.smoothnessMin);
                if (a.aoStrength >= 0f && m.HasProperty("_AORemapMin")) m.SetFloat("_AORemapMin", 1f - Mathf.Clamp01(a.aoStrength));
                if (a.emissiveScale >= 0f && m.HasProperty("_EmissiveColor") && (!m.HasProperty("_EmissiveColorMap") || m.GetTexture("_EmissiveColorMap") == null)) m.SetColor("_EmissiveColor", new Color(a.emissiveScale, a.emissiveScale, a.emissiveScale, 1f));   // absolute, only when there is no emissive map (an unmapped white _EmissiveColor glows)
                EditorUtility.SetDirty(m);
            }
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log($"HDRPFIXES done renderers+{hit} excluded={skip}");
    }
    static void BuildRimRigs(Fx F)
    {
        var old = GameObject.Find("HDRP_RimRigs"); if (old != null) Object.DestroyImmediate(old);
        if (F.rimRigs == null || F.rimRigs.Length == 0) return;
        var root = new GameObject("HDRP_RimRigs");
        foreach (var rr in F.rimRigs)
        {
            if (rr == null || rr.layerBit < 0 || rr.layerBit > 15) { Debug.LogWarning("HDRPFIXES rimRig skipped (layerBit must be 0..15)"); continue; }
            string id = RimRig.ResolveId(rr.who); string obj = Roster.Get(id).objectName; uint rb = 1u << rr.layerBit; int hit = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                string path = r.transform.root.name + "/" + AnimationUtility_GetPath(r.transform); bool inc = obj.Length > 0 && path.Contains(obj);
                if (inc && F.rimExclude != null) foreach (var x in F.rimExclude) if (!string.IsNullOrEmpty(x) && path.Contains(x)) inc = false;
                if (inc) { r.renderingLayerMask |= rb; hit++; } else r.renderingLayerMask &= ~rb;
            }
            var go = new GameObject(string.IsNullOrEmpty(rr.name) ? "Rim_" + id : rr.name); go.transform.SetParent(root.transform, false);
            var l = go.AddComponent<Light>(); var hd = go.AddComponent<HDAdditionalLightData>();
            float d = new Vector3(rr.behind, rr.above - rr.aimHeight, rr.side).magnitude; if (d < 0.1f) d = 0.1f;
            l.type = LightType.Spot; l.spotAngle = rr.spotAngle; l.range = rr.range; l.lightUnit = LightUnit.Candela; l.intensity = rr.lux * d * d;
            l.useColorTemperature = false; l.color = Mathf.CorrelatedColorTemperatureToRGB(rr.kelvin);
            l.shadows = LightShadows.None; hd.EnableShadows(false); hd.volumetricDimmer = rr.volumetricDimmer; hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)rb;
            var rig = go.AddComponent<RimRig>(); rig.who = rr.who; rig.behind = rr.behind; rig.above = rr.above; rig.side = rr.side; rig.aimHeight = rr.aimHeight;
            var t = GameObject.Find(obj); if (t != null) { var fwd = Vector3.forward; go.transform.position = t.transform.position + fwd * rr.behind + Vector3.up * rr.above; go.transform.rotation = Quaternion.LookRotation((t.transform.position + Vector3.up * rr.aimHeight - go.transform.position).normalized); }
            Debug.Log("HDRPFIXES rimRig " + go.name + " who=" + rr.who + " id=" + id + " renderers=" + hit + " cd=" + l.intensity.ToString("F0") + " K=" + rr.kelvin);
        }
    }
    // PropsRoot: rebuilt from F.props every run (replace-if-exists, idempotent). Built before the fill-layer pass so the props get the fill light layer too.
    static void BuildProps(UnityEngine.SceneManagement.Scene scene, Prop[] props)
    {
        if (props == null) return;
        foreach (var rg in scene.GetRootGameObjects()) if (rg.name == "PropsRoot") Object.DestroyImmediate(rg);
        var root = new GameObject("PropsRoot");
        int placed = 0;
        foreach (var p in props)
        {
            if (p == null || string.IsNullOrEmpty(p.glb)) continue;
            var lod0 = AssetDatabase.LoadAssetAtPath<GameObject>(p.glb);
            if (lod0 == null) { Debug.LogWarning("HDRPFIXES prop '" + p.name + "': model not found at " + p.glb + " (skipped)"); continue; }
            var holder = new GameObject(string.IsNullOrEmpty(p.name) ? lod0.name : p.name);
            holder.transform.SetParent(root.transform, false);
            holder.transform.position = p.pos != null && p.pos.Length >= 3 ? new Vector3(p.pos[0], p.pos[1], p.pos[2]) : Vector3.zero;
            holder.transform.rotation = p.euler != null && p.euler.Length >= 3 ? Quaternion.Euler(p.euler[0], p.euler[1], p.euler[2]) : Quaternion.identity;
            holder.transform.localScale = p.scale != null && p.scale.Length >= 3 ? new Vector3(p.scale[0], p.scale[1], p.scale[2]) : Vector3.one;

            var models = new System.Collections.Generic.List<GameObject> { lod0 };
            if (p.lods != null) foreach (var lp in p.lods)
            {
                var lm = string.IsNullOrEmpty(lp) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(lp);
                if (lm == null) { if (!string.IsNullOrEmpty(lp)) Debug.LogWarning("HDRPFIXES prop '" + p.name + "': LOD model not found " + lp); break; }
                models.Add(lm);
            }
            var lodRenderers = new System.Collections.Generic.List<Renderer[]>();
            for (int i = 0; i < models.Count; i++)
            {
                var inst = PrefabUtility.InstantiatePrefab(models[i]) as GameObject;
                if (inst == null) continue;
                inst.name = models[i].name;
                inst.transform.SetParent(holder.transform, false);
                inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity; inst.transform.localScale = Vector3.one;
                lodRenderers.Add(inst.GetComponentsInChildren<Renderer>());
            }
            if (lodRenderers.Count > 1)
            {
                var lg = holder.AddComponent<LODGroup>();
                var lods = new LOD[lodRenderers.Count];
                for (int i = 0; i < lods.Length; i++)
                {
                    float h = p.lodHeights != null && i < p.lodHeights.Length ? p.lodHeights[i] : Mathf.Pow(0.4f, i + 1);
                    lods[i] = new LOD(h, lodRenderers[i]);
                }
                lg.SetLODs(lods); lg.RecalculateBounds();
            }
            placed++;
        }
        Debug.Log("HDRPFIXES props placed=" + placed);
    }
    // ClashFxSheets: holds the baked ClashFx flipbook textures (paths from clash_fx.json "sheets") in the scene so player builds include them; rebuilt every run.
    static void BuildClashSheets(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (var rg in scene.GetRootGameObjects()) if (rg.name == "ClashFxSheets") Object.DestroyImmediate(rg);
        ClashFxTable.Reload();
        var sh = ClashFxTable.Get().sheets;
        if (sh == null || sh.enabled == 0) return;
        var go = new GameObject("ClashFxSheets");
        var r = go.AddComponent<ClashFxSheetRefs>();
        if (sh.sparks != null) { r.sparks = LoadTex(sh.sparks.texture); r.sparksHeavy = LoadTex(sh.sparks.textureHeavy); }
        if (sh.dust != null) { r.dust = LoadTex(sh.dust.texture); r.dustMotion = LoadTex(sh.dust.motion); }
        Debug.Log("HDRPFIXES ClashFxSheets sparks=" + (r.sparks != null) + " heavy=" + (r.sparksHeavy != null) + " dust=" + (r.dust != null) + " dustMv=" + (r.dustMotion != null));
    }
    static Texture2D LoadTex(string path) { return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path); }
    static string AnimationUtility_GetPath(Transform t) { string p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }
}

