using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Wound decals: a cut / puncture mark placed ON the victim's body at the hit point, aligned to the swing, parented to the nearest bone so it moves with the body.
// slash (light_left / light_right ...) = horizontal streak tilted by the swing side, thrust / riposte = round puncture, overhead / heavy = vertical streak (all shapes in wounds.json).
// WOUNDLOOK v96: wet dark-red look from wounds.json decal.* (colour ramp, normal groove, AO, smoothness), see MapSet.
// Real HDRP DecalProjector (HDRP/Decal material: albedo + smoothness mask), dark blood base ~(0.25, 0.02, 0.02), smoothness 0.8, all from wounds.json "decal".
// ART GAP: no authored textures exist, so the streak / puncture masks are generated here in code (WoundDecals.MakeMask). Replace with painted masks later.
// If the HDRP/Decal shader is missing from the player (not in Always Included Shaders), a bone-parented blood quad on BloodGround's material is used instead (decal.quadFallback).
// Presentation only: never touches the sim. State lives in WoundSystem (one WoundDecals.Set per fighter).
public static class WoundDecals
{
    public class Decal { public Transform t; public DecalProjector proj; public MeshRenderer mr; public float born, fadeStart = -1f, baseOpacity; }
    public class Set { public List<Decal> list = new List<Decal>(); public Transform[] bones; public uint seed = 7; }

    static readonly Dictionary<string, Texture2D[]> maskCache = new Dictionary<string, Texture2D[]>();   // key = shape tex name; straight-alpha colour texture per variant
    static readonly Dictionary<string, Texture2D[]> premulCache = new Dictionary<string, Texture2D[]>(); // premultiplied copy for the quad fallback
    static readonly List<Material> decalMats = new List<Material>();
    static Material quadMat; static Mesh quad; static Shader decalShader; static bool shaderLooked, warned, decalSrcLogged;   // QC1 v95
    static readonly int TintId = Shader.PropertyToID("_Tint"), MainTexId = Shader.PropertyToID("_MainTex");
    static MaterialPropertyBlock mpb;

    static float Rnd(Set s) { unchecked { s.seed += 0x6d2b79f5u; uint t = (s.seed ^ (s.seed >> 15)) * (1u | s.seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }
    static float Between(Set s, float lo, float hi) { return lo + (hi - lo) * Rnd(s); }

    // ---------------- textures (procedural; ART GAP) ----------------
    // streak: a long tapered gash with a thick core, ragged edge noise and a few drops at the ends. puncture: a round hole with a short radial run and satellite drops.
    static float[] MakeMask(string kind, int n, int seed)
    {
        var rnd = new System.Random(seed); var a = new float[n * n]; var W = BloodShapes.Get().wound;   // HCKIT6 v100: every shape number is blood_shapes.json wound{} (RNG order unchanged)
        if (kind == "puncture")
        {
            AddBlob(a, n, W.centre, W.centre, W.coreR, 1f, 1f, 0f);
            float runAng = (float)(rnd.NextDouble() * Mathf.PI * 2f);
            for (int i = 0; i < W.runCount; i++) { float d = W.runStart + i * W.runStep; AddBlob(a, n, W.centre + Mathf.Cos(runAng) * d, W.centre + Mathf.Sin(runAng) * d, W.runR - i * W.runRStep, 1f, 1f, 0f); }
            for (int i = 0; i < W.satCount; i++) { float ang = (float)(rnd.NextDouble() * Mathf.PI * 2f), d = W.satDistMin + (float)rnd.NextDouble() * W.satDistRange; AddBlob(a, n, W.centre + Mathf.Cos(ang) * d, W.centre + Mathf.Sin(ang) * d, W.satRMin + (float)rnd.NextDouble() * W.satRRange, 1f, 1f, 0f); }
        }
        else
        {
            int seg = W.seg;
            for (int i = 0; i < seg; i++)
            {
                float u = i / (float)(seg - 1), x = W.segX0 + u * W.segXSpan, taper = Mathf.Sin(u * Mathf.PI), wob = (float)(rnd.NextDouble() - 0.5) * W.wobble;
                AddBlob(a, n, x, W.centre + wob + Mathf.Sin(u * W.sineFreq + seed) * W.sineAmp, W.segR + W.segTaper * taper * (W.segJitterLo + W.segJitterRange * (float)rnd.NextDouble()), W.segSx, 1f, 0f);
            }
            for (int i = 0; i < W.speckCount; i++) AddBlob(a, n, W.speckX0 + (float)rnd.NextDouble() * W.speckXSpan, W.centre + ((float)rnd.NextDouble() - 0.5f) * W.speckYSpan, W.speckRMin + (float)rnd.NextDouble() * W.speckRRange, 1f, 1f, 0f);
            for (int i = 0; i < W.dripCount; i++) AddBlob(a, n, W.dripX0 + (float)rnd.NextDouble() * W.dripXSpan, W.centre - W.dripDrop - (float)rnd.NextDouble() * W.dripDropRange, W.dripR, W.dripSx, W.dripSy, 0f);   // short runs below the cut
        }
        for (int i = 0; i < a.Length; i++) a[i] = Mathf.Clamp01(a[i] * BloodShapes.Get().wound.gain);   // HCKIT6 v100
        return a;
    }

    static void AddBlob(float[] a, int n, float cx, float cy, float r, float sx, float sy, float unused)
    {
        float reach = BloodShapes.Get().wound.blobReach;   // HCKIT6 v100: was 1.2f
        int x0 = Mathf.Max(0, (int)((cx - r * sx * reach) * n)), x1 = Mathf.Min(n - 1, (int)((cx + r * sx * reach) * n)), y0 = Mathf.Max(0, (int)((cy - r * sy * reach) * n)), y1 = Mathf.Min(n - 1, (int)((cy + r * sy * reach) * n));
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            float dx = ((x + 0.5f) / n - cx) / (r * sx), dy = ((y + 0.5f) / n - cy) / (r * sy), q = dx * dx + dy * dy;
            if (q < 1f) { float v = 1f - q; v = v * v * (3f - 2f * v); if (v > a[y * n + x]) a[y * n + x] = v; }
        }
    }

    // WOUNDLOOK v96: per (shape, variant) HDRP decal maps built from the same height mask: base colour (ramp edge->core relative to decal.color, alpha = mask), normal (groove + raised lips), mask (R metal 0, G AO, B blend, A smoothness: wet core)
    class Maps { public Texture2D color, normal, mask; }
    static readonly Dictionary<string, Maps[]> mapCache = new Dictionary<string, Maps[]>();
    static float[] Blur(float[] src, int n, int r)
    {
        if (r < 1) return (float[])src.Clone(); var tmp = new float[src.Length]; var dst = new float[src.Length];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float sum = 0; int c = 0; for (int k = -r; k <= r; k++) { int xx = x + k; if (xx < 0 || xx >= n) continue; sum += src[y * n + xx]; c++; } tmp[y * n + x] = sum / c; }
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float sum = 0; int c = 0; for (int k = -r; k <= r; k++) { int yy = y + k; if (yy < 0 || yy >= n) continue; sum += tmp[yy * n + x]; c++; } dst[y * n + x] = sum / c; }
        return dst;
    }
    static byte B8(float v) { return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f); }
    static Maps[] MapSet(string kind, WoundTable.DecalCfg dc)
    {
        Maps[] arr; if (mapCache.TryGetValue(kind, out arr)) return arr;
        var W = BloodShapes.Get().wound; int n = Mathf.Clamp(dc.texSize, W.texMin, W.texMax), vc = Mathf.Max(1, dc.variants);   /* HCKIT6 v100 */ arr = new Maps[vc];
        int r1 = Mathf.Max(1, Mathf.RoundToInt(dc.normalBlur * n)), r2 = r1 * 3;
        for (int v = 0; v < vc; v++)
        {
            var a = MakeMask(kind, n, dc.seed * W.seedMul + v * W.variantMul + (kind == "puncture" ? W.punctureSeedAdd : 0));
            var h1 = Blur(a, n, r1); var h2 = Blur(a, n, r2);
            var h = new float[a.Length]; for (int i = 0; i < h.Length; i++) h[i] = -h1[i] + dc.lipHeight * Mathf.Max(0f, h2[i] - h1[i]) * 2f;   // groove inside, raised lip just outside
            var cpx = new Color32[a.Length]; var npx = new Color32[a.Length]; var mpx = new Color32[a.Length];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                int i = y * n + x; float m = a[i], core = Mathf.Clamp01((h1[i] - 0.35f) / 0.5f);   // core = deep inside the cut
                float al = Mathf.Pow(m, Mathf.Max(0.05f, dc.alphaGamma));
                cpx[i] = new Color32(B8(Mathf.Lerp(dc.edgeColor[0], dc.coreColor[0], core)), B8(Mathf.Lerp(dc.edgeColor[1], dc.coreColor[1], core)), B8(Mathf.Lerp(dc.edgeColor[2], dc.coreColor[2], core)), B8(al));
                int xl = Mathf.Max(0, x - 1), xr = Mathf.Min(n - 1, x + 1), yd = Mathf.Max(0, y - 1), yu = Mathf.Min(n - 1, y + 1);
                float dx = (h[y * n + xr] - h[y * n + xl]) * 0.5f * n * dc.normalStrength / 32f, dy = (h[yu * n + x] - h[yd * n + x]) * 0.5f * n * dc.normalStrength / 32f;
                var nv = new Vector3(-dx, -dy, 1f).normalized; npx[i] = new Color32(B8(nv.x * 0.5f + 0.5f), B8(nv.y * 0.5f + 0.5f), B8(nv.z * 0.5f + 0.5f), 255);
                mpx[i] = new Color32(0, B8(Mathf.Lerp(1f, dc.aoCore, core)), B8(al), B8(Mathf.Lerp(dc.smoothnessEdge, dc.smoothness, core)));
            }
            var mp = new Maps();
            mp.color = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = "Wound_" + kind + v + "_c", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear }; mp.color.SetPixels32(cpx); mp.color.Apply(true, true);
            mp.normal = new Texture2D(n, n, TextureFormat.RGBA32, true, true) { name = "Wound_" + kind + v + "_n", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear }; mp.normal.SetPixels32(npx); mp.normal.Apply(true, true);
            mp.mask = new Texture2D(n, n, TextureFormat.RGBA32, true, true) { name = "Wound_" + kind + v + "_m", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear }; mp.mask.SetPixels32(mpx); mp.mask.Apply(true, true);
            arr[v] = mp;
        }
        mapCache[kind] = arr; return arr;
    }

    // quad fallback only: premultiplied colour mask in the json colour
    static Texture2D[] Masks(string kind, WoundTable.DecalCfg dc, bool premul)
    {
        var cache = premul ? premulCache : maskCache; Texture2D[] arr;
        if (cache.TryGetValue(kind, out arr)) return arr;
        var W = BloodShapes.Get().wound; int n = Mathf.Clamp(dc.texSize, W.texMin, W.texMax), vc = Mathf.Max(1, dc.variants);   /* HCKIT6 v100 */ arr = new Texture2D[vc];
        for (int v = 0; v < vc; v++)
        {
            var a = MakeMask(kind, n, dc.seed * W.seedMul + v * W.variantMul + (kind == "puncture" ? W.punctureSeedAdd : 0)); var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                float al = a[i]; byte c0 = B8(dc.color[0]), c1 = B8(dc.color[1]), c2 = B8(dc.color[2]), a8 = B8(al);
                px[i] = premul ? new Color32((byte)(c0 * al), (byte)(c1 * al), (byte)(c2 * al), a8) : new Color32(c0, c1, c2, a8);
            }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = "Wound_" + kind + v + (premul ? "_pm" : ""), wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.SetPixels32(px); t.Apply(true, true); arr[v] = t;
        }
        cache[kind] = arr; return arr;
    }

    // one HDRP/Decal material per (shape, variant); null = shader unavailable
    static Material DecalMat(string kind, int variant, WoundTable.DecalCfg dc)
    {
        if (!shaderLooked) { shaderLooked = true; decalShader = Shader.Find("HDRP/Decal"); }
        if (decalShader == null) return null;
        string key = kind + "#" + variant; for (int i = decalMats.Count - 1; i >= 0; i--) { if (decalMats[i] == null) { decalMats.RemoveAt(i); continue; } if (decalMats[i].name == key) return decalMats[i]; }   // v75 leak fix: a destroyed material is dropped, never handed to a projector
        var refMat = Resources.Load<Material>("WoundDecalRef");   // QC1 v95
        var m = refMat != null ? new Material(refMat) : new Material(decalShader);   // QC1 v95
        m.name = key; m.hideFlags = HideFlags.DontUnloadUnusedAsset; m.enableInstancing = true;   // QC1 v95
        m.EnableKeyword("_COLORMAP"); m.EnableKeyword("_MASKMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); m.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");   // QC1 v95
        if (!decalSrcLogged) { decalSrcLogged = true; Debug.Log("[WoundDecals] decal material from " + (refMat != null ? "Resources/WoundDecalRef" : "Shader.Find (no reference asset: variants may be stripped)")); }   // QC1 v95
        // WOUNDLOOK v96: wet dark-red cut. _BaseColor = json colour (never white: if the _COLORMAP variant is stripped the flat decal is still dark red); colour map is a relative ramp; normal map = groove + lips; mask map = AO / blend / wet smoothness
        var mp = MapSet(kind, dc); var mset = mp[variant % mp.Length];
        m.SetColor("_BaseColor", new Color(dc.color[0], dc.color[1], dc.color[2], 1f)); m.SetTexture("_BaseColorMap", mset.color);
        m.SetTexture("_MaskMap", mset.mask);
        if (dc.affectNormal) { m.SetTexture("_NormalMap", mset.normal); m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_NORMAL"); }   // these variants only ship if WoundDecalRef also enables them (editor script)
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", dc.affectNormal ? 1f : 0f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", dc.affectAO ? 1f : 0f); m.SetFloat("_AffectSmoothness", 1f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", Mathf.Clamp01(dc.decalBlend)); m.SetFloat("_DecalMaskMapBlueScale", Mathf.Clamp01(dc.maskBlueScale));
        m.SetFloat("_NormalBlendSrc", 0f); m.SetFloat("_MaskBlendSrc", 1f);
        if (m.HasProperty("_SmoothnessRemapMin")) m.SetFloat("_SmoothnessRemapMin", 0f);
        if (m.HasProperty("_SmoothnessRemapMax")) m.SetFloat("_SmoothnessRemapMax", 1f);
        decalMats.Add(m); return m;
    }

    // PREWARM v92: one HDRP/Decal material per wound shape (variant 0) so the first wound does not compile the decal shader / upload the generated masks; Prewarm92 shows them on a sub-pixel DecalProjector.
    internal static void CollectPrewarm(List<Material> into)
    {
        var W = WoundTable.Get(); if (W == null || !W.enabled || !W.decals) return; var seen = new HashSet<string>();
        foreach (var r in W.characters)
            foreach (var sh in new[] { r.slash, r.stab, r.overhead })
                if (sh != null && seen.Add(sh.tex)) { var m = DecalMat(sh.tex, 0, W.decal); if (m != null) into.Add(m); }
    }
    // PREWARM v92 end
    static Mesh Quad()
    {
        if (quad != null) return quad;
        quad = new Mesh { name = "WoundQuad" };
        quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
        quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 }; quad.RecalculateNormals(); quad.RecalculateBounds(); return quad;
    }

    // ---------------- placement ----------------
    static Transform[] BonesOf(DuelFighter f)
    {
        var set = new HashSet<Transform>();
        foreach (var smr in f.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (smr.bones != null) foreach (var b in smr.bones) if (b != null) set.Add(b);
        var arr = new Transform[set.Count]; set.CopyTo(arr); return arr;
    }
    static Transform Nearest(Set s, DuelFighter f, Vector3 p)
    {
        if (s.bones == null || s.bones.Length == 0 || s.bones[0] == null) s.bones = BonesOf(f);
        Transform best = null; float bd = float.MaxValue;
        for (int i = 0; i < s.bones.Length; i++) { var b = s.bones[i]; if (b == null) continue; float d = (b.position - p).sqrMagnitude; if (d < bd) { bd = d; best = b; } }
        return best != null ? best : f.transform;
    }

    // shape for the swing: thrust = stab, overhead / heavy = vertical, else slash
    public static string KindOf(MoveDef m, string moveId)
    {
        string dir = m != null && m.direction != null ? m.direction : "", id = moveId ?? "";
        if (dir == "thrust" || id.Contains("thrust") || id == "riposte") return "stab";
        if (dir == "overhead" || id.Contains("overhead") || id.StartsWith("heavy")) return "overhead";
        return "slash";
    }

    // Returns the new decal (null when nothing was placed). `ev` is the Hit; `kill` / `counter` scale the size.
    public static Decal Spawn(Set s, DuelFighter victim, CombatEvent ev, WoundTable.Row row, WoundTable.DecalCfg dc, bool kill)
    {
        if (victim == null || ev.actor == null) return null;
        string kind = KindOf(ev.actor.CurrentMove, ev.moveId);
        var shape = kind == "stab" ? row.stab : kind == "overhead" ? row.overhead : row.slash;
        float sc = Mathf.Max(0.01f, victim.scale);
        string loc = !string.IsNullOrEmpty(ev.hitLocation) ? ev.hitLocation : ev.location;
        float h = loc == "head" ? row.headY : (loc == "arm" || loc == "arms") ? row.armY : loc == "legs" ? row.legsY : row.torsoY;
        h += Between(s, -row.heightJitter, row.heightJitter);
        Vector3 a2v = victim.transform.position - ev.actor.transform.position; a2v.y = 0f; a2v = a2v.sqrMagnitude > 1e-4f ? a2v.normalized : victim.transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, a2v);   // attacker-facing frame, +right = victim's left from the attacker's view; side is mirrored by hitDirection below
        float side = ev.direction == "left" ? -1f : ev.direction == "right" ? 1f : Between(s, -1f, 1f) * 0.4f;
        Vector3 point = victim.transform.position + Vector3.up * h * sc - a2v * row.bodyRadius * sc + right * (side * row.lateral + Between(s, -row.lateralJitter, row.lateralJitter)) * sc;
        // v69 (B1 tier a): put the mark on the victim's real surface, not on a guessed body radius: ray along the swing against the victim's colliders; no hit = the old guessed point
        bool onSurface = false;
        { var cols = victim.GetComponentsInChildren<Collider>(); float best = float.MaxValue; var ray = new Ray(point - a2v * 1.0f * sc, a2v);
          for (int ci = 0; ci < cols.Length; ci++) { var c = cols[ci]; if (c == null || !c.enabled || c.isTrigger) continue; RaycastHit hi; if (c.Raycast(ray, out hi, 2.5f * sc) && hi.distance < best) { best = hi.distance; point = hi.point; onSurface = true; } } }
        float roll = shape.baseRollDeg;
        if (kind == "slash") roll += (ev.direction == "left" ? 1f : ev.direction == "right" ? -1f : (Rnd(s) < 0.5f ? 1f : -1f)) * Between(s, 0.4f, 1f) * shape.tiltDeg;
        else roll += Between(s, -1f, 1f) * shape.tiltDeg;
        float mul = Between(s, 1f - row.sizeJitter, 1f + row.sizeJitter) * (ev.heavy ? row.heavySizeMul : 1f) * (ev.counter ? row.counterSizeMul : 1f) * (kill ? row.killSizeMul : 1f) * sc;
        float len = shape.length * mul, wid = shape.width * mul;
        int variant = (int)(Rnd(s) * 1000f) % Mathf.Max(1, dc.variants);

        var bone = Nearest(s, victim, point);
        var go = new GameObject("WoundDecal_" + kind); go.SetActive(false);   // v75 leak fix: inactive until the projector has its material
        Quaternion rot = Quaternion.LookRotation(a2v, Vector3.up) * Quaternion.AngleAxis(roll, Vector3.forward);
        var d = new Decal { t = go.transform, born = Time.unscaledTime, baseOpacity = Mathf.Clamp01(row.opacity) };
        var mat = DecalMat(shape.tex, variant, dc);
        if (mat != null)
        {
            go.transform.SetPositionAndRotation(onSurface ? point : point - a2v * row.depth * 0.35f * sc, rot);
            var p = go.AddComponent<DecalProjector>();
            p.material = mat; p.size = new Vector3(len, wid, row.depth * sc); p.pivot = Vector3.zero; p.fadeFactor = d.baseOpacity;
            p.drawDistance = dc.drawDistance; p.fadeScale = dc.fadeScale; p.startAngleFade = dc.startAngleFade; p.endAngleFade = dc.endAngleFade;
            d.proj = p;
        }
        else if (dc.quadFallback)
        {
            if (quadMat == null) { quadMat = FxMaterials.Particle(null, "blood"); if (quadMat != null) quadMat.name = "FxMat_wound"; }
            if (quadMat == null) { Object.Destroy(go); if (!warned) { warned = true; Debug.LogWarning("WoundDecals: no HDRP/Decal shader and no particle material, wound decals off."); } return null; }
            if (!warned) { warned = true; Debug.LogWarning("WoundDecals: HDRP/Decal shader missing from the player, using bone-parented quads (add HDRP/Decal to Always Included Shaders)."); }
            go.transform.SetPositionAndRotation(point - a2v * 0.006f * sc, rot); go.transform.localScale = new Vector3(len, wid, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = Quad(); var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = quadMat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            if (mpb == null) mpb = new MaterialPropertyBlock();
            var tx = Masks(shape.tex, dc, true); mpb.Clear(); mpb.SetTexture(MainTexId, tx[variant % tx.Length]); mpb.SetColor(TintId, new Color(d.baseOpacity, d.baseOpacity, d.baseOpacity, d.baseOpacity)); mr.SetPropertyBlock(mpb);
            d.mr = mr;
        }
        else { Object.Destroy(go); return null; }
        // parent to the nearest bone, keeping the world pose; cancel the bone's (possibly non-1) scale so the projector box keeps its metre size
        Vector3 want = go.transform.localScale;
        go.transform.SetParent(bone, true);
        var ps = bone.lossyScale;
        go.transform.localScale = new Vector3(want.x / Mathf.Max(1e-4f, ps.x), want.y / Mathf.Max(1e-4f, ps.y), want.z / Mathf.Max(1e-4f, ps.z));
        go.SetActive(true);
        s.list.Add(d); return d;
    }

    static void SetOpacity(Decal d, float f)
    {
        float o = Mathf.Clamp01(f) * d.baseOpacity;
        if (d.proj != null) d.proj.fadeFactor = o;
        else if (d.mr != null) { if (mpb == null) mpb = new MaterialPropertyBlock(); d.mr.GetPropertyBlock(mpb); mpb.SetColor(TintId, new Color(o, o, o, o)); d.mr.SetPropertyBlock(mpb); }
    }

    // cap: the oldest live decals beyond `max` start to fade (they stop counting at once); returns how many were evicted
    public static int Enforce(Set s, int max, float now)
    {
        int live = 0, evicted = 0; for (int i = 0; i < s.list.Count; i++) if (s.list[i].fadeStart < 0f) live++;
        for (int i = 0; i < s.list.Count && live > Mathf.Max(0, max); i++) { var d = s.list[i]; if (d.fadeStart >= 0f) continue; d.fadeStart = now; live--; evicted++; }
        return evicted;
    }

    public static int Live(Set s) { int n = 0; for (int i = 0; i < s.list.Count; i++) if (s.list[i].fadeStart < 0f) n++; return n; }

    public static void Tick(Set s, WoundTable.Row row, float now)
    {
        for (int i = s.list.Count - 1; i >= 0; i--)
        {
            var d = s.list[i];
            if (d.t == null) { s.list.RemoveAt(i); continue; }   // bone destroyed with the victim
            if (d.fadeStart < 0f && row.lifeSec > 0f && now - d.born >= row.lifeSec) d.fadeStart = now;
            if (d.fadeStart < 0f) continue;
            float f = 1f - (now - d.fadeStart) / Mathf.Max(0.01f, row.fadeOutSec);
            if (f <= 0f) { Object.Destroy(d.t.gameObject); s.list.RemoveAt(i); } else SetOpacity(d, f);
        }
    }

    public static void Clear(Set s) { for (int i = 0; i < s.list.Count; i++) if (s.list[i].t != null) Object.Destroy(s.list[i].t.gameObject); s.list.Clear(); }
}
