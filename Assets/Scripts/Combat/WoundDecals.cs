using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Wound decals: a cut / puncture mark placed ON the victim's body at the hit point, aligned to the swing, parented to the nearest bone so it moves with the body.
// slash (light_left / light_right ...) = horizontal streak tilted by the swing side, thrust / riposte = round puncture, overhead / heavy = vertical streak (all shapes in wounds.json).
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
    static Texture2D maskMap;                                                                             // HDRP decal mask: smoothness in alpha
    static readonly List<Material> decalMats = new List<Material>();
    static Material quadMat; static Mesh quad; static Shader decalShader; static bool shaderLooked, warned;
    static readonly int TintId = Shader.PropertyToID("_Tint"), MainTexId = Shader.PropertyToID("_MainTex");
    static MaterialPropertyBlock mpb;

    static float Rnd(Set s) { unchecked { s.seed += 0x6d2b79f5u; uint t = (s.seed ^ (s.seed >> 15)) * (1u | s.seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }
    static float Between(Set s, float lo, float hi) { return lo + (hi - lo) * Rnd(s); }

    // ---------------- textures (procedural; ART GAP) ----------------
    // streak: a long tapered gash with a thick core, ragged edge noise and a few drops at the ends. puncture: a round hole with a short radial run and satellite drops.
    static float[] MakeMask(string kind, int n, int seed)
    {
        var rnd = new System.Random(seed); var a = new float[n * n];
        if (kind == "puncture")
        {
            AddBlob(a, n, 0.5f, 0.5f, 0.1f, 1f, 1f, 0f);
            float runAng = (float)(rnd.NextDouble() * Mathf.PI * 2f);
            for (int i = 0; i < 6; i++) { float d = 0.1f + i * 0.045f; AddBlob(a, n, 0.5f + Mathf.Cos(runAng) * d, 0.5f + Mathf.Sin(runAng) * d, 0.04f - i * 0.004f, 1f, 1f, 0f); }
            for (int i = 0; i < 9; i++) { float ang = (float)(rnd.NextDouble() * Mathf.PI * 2f), d = 0.16f + (float)rnd.NextDouble() * 0.25f; AddBlob(a, n, 0.5f + Mathf.Cos(ang) * d, 0.5f + Mathf.Sin(ang) * d, 0.012f + (float)rnd.NextDouble() * 0.025f, 1f, 1f, 0f); }
        }
        else
        {
            int seg = 28;
            for (int i = 0; i < seg; i++)
            {
                float u = i / (float)(seg - 1), x = 0.06f + u * 0.88f, taper = Mathf.Sin(u * Mathf.PI), wob = (float)(rnd.NextDouble() - 0.5) * 0.04f;
                AddBlob(a, n, x, 0.5f + wob + Mathf.Sin(u * 5f + seed) * 0.03f, 0.03f + 0.07f * taper * (0.75f + 0.5f * (float)rnd.NextDouble()), 1.6f, 1f, 0f);
            }
            for (int i = 0; i < 10; i++) AddBlob(a, n, 0.1f + (float)rnd.NextDouble() * 0.8f, 0.5f + ((float)rnd.NextDouble() - 0.5f) * 0.4f, 0.01f + (float)rnd.NextDouble() * 0.02f, 1f, 1f, 0f);
            for (int i = 0; i < 3; i++) AddBlob(a, n, 0.2f + (float)rnd.NextDouble() * 0.6f, 0.5f - 0.08f - (float)rnd.NextDouble() * 0.12f, 0.012f, 0.6f, 2.4f, 0f);   // short runs below the cut
        }
        for (int i = 0; i < a.Length; i++) a[i] = Mathf.Clamp01(a[i] * 1.5f);
        return a;
    }

    static void AddBlob(float[] a, int n, float cx, float cy, float r, float sx, float sy, float unused)
    {
        int x0 = Mathf.Max(0, (int)((cx - r * sx * 1.2f) * n)), x1 = Mathf.Min(n - 1, (int)((cx + r * sx * 1.2f) * n)), y0 = Mathf.Max(0, (int)((cy - r * sy * 1.2f) * n)), y1 = Mathf.Min(n - 1, (int)((cy + r * sy * 1.2f) * n));
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            float dx = ((x + 0.5f) / n - cx) / (r * sx), dy = ((y + 0.5f) / n - cy) / (r * sy), q = dx * dx + dy * dy;
            if (q < 1f) { float v = 1f - q; v = v * v * (3f - 2f * v); if (v > a[y * n + x]) a[y * n + x] = v; }
        }
    }

    static Texture2D[] Masks(string kind, WoundTable.DecalCfg dc, bool premul)
    {
        var cache = premul ? premulCache : maskCache; Texture2D[] arr;
        if (cache.TryGetValue(kind, out arr)) return arr;
        int n = Mathf.Clamp(dc.texSize, 16, 256), vc = Mathf.Max(1, dc.variants); arr = new Texture2D[vc];
        for (int v = 0; v < vc; v++)
        {
            var a = MakeMask(kind, n, dc.seed * 131 + v * 7919 + (kind == "puncture" ? 17 : 0)); var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                float al = a[i];
                byte c0 = (byte)Mathf.RoundToInt(Mathf.Clamp01(dc.color[0]) * 255f), c1 = (byte)Mathf.RoundToInt(Mathf.Clamp01(dc.color[1]) * 255f), c2 = (byte)Mathf.RoundToInt(Mathf.Clamp01(dc.color[2]) * 255f), a8 = (byte)Mathf.RoundToInt(al * 255f);
                px[i] = premul ? new Color32((byte)(c0 * al), (byte)(c1 * al), (byte)(c2 * al), a8) : new Color32(c0, c1, c2, a8);
            }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = "Wound_" + kind + v + (premul ? "_pm" : ""), wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.SetPixels32(px); t.Apply(true, true); arr[v] = t;
        }
        cache[kind] = arr; return arr;
    }

    static Texture2D MaskMap(WoundTable.DecalCfg dc)
    {
        if (maskMap != null) return maskMap;
        // HDRP decal mask map: R metallic, G AO, B mask opacity, A smoothness
        var px = new Color32[4]; byte sm = (byte)Mathf.RoundToInt(Mathf.Clamp01(dc.smoothness) * 255f);
        for (int i = 0; i < 4; i++) px[i] = new Color32(0, 255, 255, sm);
        maskMap = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { name = "Wound_Mask", wrapMode = TextureWrapMode.Clamp }; maskMap.SetPixels32(px); maskMap.Apply(false, true); return maskMap;
    }

    // one HDRP/Decal material per (shape, variant); null = shader unavailable
    static Material DecalMat(string kind, int variant, WoundTable.DecalCfg dc)
    {
        if (!shaderLooked) { shaderLooked = true; decalShader = Shader.Find("HDRP/Decal"); }
        if (decalShader == null) return null;
        string key = kind + "#" + variant; for (int i = decalMats.Count - 1; i >= 0; i--) { if (decalMats[i] == null) { decalMats.RemoveAt(i); continue; } if (decalMats[i].name == key) return decalMats[i]; }   // v75 leak fix: a destroyed material is dropped, never handed to a projector
        var m = new Material(decalShader) { name = key, hideFlags = HideFlags.DontUnloadUnusedAsset, enableInstancing = true };
        var tex = Masks(kind, dc, false); m.SetTexture("_BaseColorMap", tex[variant % tex.Length]); m.SetColor("_BaseColor", Color.white);
        m.SetTexture("_MaskMap", MaskMap(dc));
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", 0f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", 0f); m.SetFloat("_AffectSmoothness", 1f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f);
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
