using System;
using System.Collections.Generic;
using UnityEngine;

// Blood art for the finisher cut: spray strands + the decals splashed at the cut. (Was GoreFx / WoundsRuntime; the per-hit wound rules and severity state are gone.)
public static class GoreFx
{
    static Texture2D[] splats; static Material decalMat, sprayMat; static Mesh quad; static bool warned;

    public static Mesh Quad()
    {
        if (quad != null) return quad;
        quad = new Mesh { name = "FinisherBloodQuad" };
        quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
        quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quad.RecalculateBounds();
        return quad;
    }

    // soft irregular splat: a few random discs plus satellite drops, premultiplied alpha (Furnace/Particle blend One, OneMinusSrcAlpha)
    public static Texture2D[] Splats(GoreCfg g)
    {
        if (splats != null) return splats;
        int n = Mathf.Clamp(g.texSize, 16, 256);
        splats = new Texture2D[4];
        for (int v = 0; v < splats.Length; v++)
        {
            var rnd = new System.Random(g.seed * 31 + v * 7919);
            var a = new float[n * n];
            int blobs = 5 + rnd.Next(4);
            for (int b = 0; b < blobs; b++)
            {
                float cx = 0.5f + (float)(rnd.NextDouble() - 0.5) * 0.5f, cy = 0.5f + (float)(rnd.NextDouble() - 0.5) * 0.5f;
                float r = 0.07f + (float)rnd.NextDouble() * (b == 0 ? 0.22f : 0.12f);
                AddBlob(a, n, cx, cy, r);
            }
            int drops = 6 + rnd.Next(8);
            for (int d = 0; d < drops; d++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, dist = 0.25 + rnd.NextDouble() * 0.2;
                AddBlob(a, n, 0.5f + (float)(Math.Cos(ang) * dist), 0.5f + (float)(Math.Sin(ang) * dist), 0.012f + (float)rnd.NextDouble() * 0.03f);
            }
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                float al = Mathf.Clamp01(a[i] * 1.6f);
                byte al8 = (byte)Mathf.RoundToInt(al * 255f);
                px[i] = new Color32(al8, al8, al8, al8);   // white premultiplied; tint colour comes from the material
            }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "FinisherSplat" + v, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px); t.Apply(false, true);
            splats[v] = t;
        }
        return splats;
    }

    static void AddBlob(float[] a, int n, float cx, float cy, float r)
    {
        int x0 = Mathf.Max(0, (int)((cx - r) * n)), x1 = Mathf.Min(n - 1, (int)((cx + r) * n) + 1), y0 = Mathf.Max(0, (int)((cy - r) * n)), y1 = Mathf.Min(n - 1, (int)((cy + r) * n) + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / n - cx, dy = (y + 0.5f) / n - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                if (d >= 1f) continue;
                float v = Mathf.Clamp01((1f - d) * 3f);
                if (v > a[y * n + x]) a[y * n + x] = v;
            }
    }

    /// <summary>Decal material (Furnace/Particle, premultiplied alpha, tinted dark red). Null when the shader is missing.</summary>
    public static Material DecalMaterial(GoreCfg g, int variant)
    {
        var sh = FxMaterials.FindParticleShader();
        if (sh == null) { if (!warned) { warned = true; Debug.LogWarning("GoreFx: Furnace/Particle shader not found; blood decals disabled."); } return null; }
        var tex = Splats(g)[variant % 4];
        var m = new Material(sh) { name = "FinisherBloodDecal" + variant };
        m.SetTexture("_MainTex", tex);
        var c = g.color; m.SetColor("_Tint", new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f));
        m.SetFloat("_Intensity", 1f); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", 0f);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 10f);
        m.renderQueue = 3000;
        return m;
    }

    static Material[] decalMats;
    public static Material SharedDecalMaterial(GoreCfg g, int variant)
    {
        if (decalMats == null) decalMats = new Material[4];
        int i = variant & 3;
        if (decalMats[i] == null) decalMats[i] = DecalMaterial(g, i);
        return decalMats[i];
    }

    /// <summary>Directional blood spray (stretched strands like BloodBurst). The emitter is a child of `parent` so it follows the body. Returns null if no material.</summary>
    public static ParticleSystem Spray(Transform parent, Vector3 worldPos, Vector3 worldDir, SprayCfg c, float secondsOverride = 0f)
    {
        if (sprayMat == null) sprayMat = FxMaterials.Particle(null, "bloodStrands");
        if (sprayMat == null) return null;
        float secs = secondsOverride > 0f ? secondsOverride : c.seconds;
        var go = new GameObject("FinisherBloodSpray");
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.LookRotation(worldDir.sqrMagnitude > 1e-6f ? worldDir.normalized : Vector3.up);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.duration = secs; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = c.gravity / 9.81f; main.maxParticles = 600;
        main.startLifetime = new ParticleSystem.MinMaxCurve(c.life[0], c.life[1]);
        main.startSpeed = new ParticleSystem.MinMaxCurve(c.speed[0], c.speed[1]);
        main.startSize = new ParticleSystem.MinMaxCurve(c.size[0], c.size[1]);
        var em = ps.emission; em.enabled = true;
        var decay = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.05f));   // jet tapers to drips
        em.rateOverTime = new ParticleSystem.MinMaxCurve(c.rate, decay);
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = c.coneDeg; sh.radius = 0.01f;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(new Color(0.412f, 0.059f, 0.051f), 0f), new GradientColorKey(new Color(0.125f, 0.02f, 0.016f), 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = sprayMat; r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 3.2f; r.velocityScale = 0f; r.cameraVelocityScale = 0f; r.maxParticleSize = 0.5f;
        ps.Play();
        UnityEngine.Object.Destroy(go, secs + c.life[1] + 2f);
        return ps;
    }

    /// <summary>Bone name key: namespace prefix ("mixamorig:") dropped, lower case.</summary>
    public static string Key(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
        return n.ToLowerInvariant();
    }

    static Transform FindBone(Dictionary<string, Transform> lookup, string[] names)
    {
        if (names == null) return null;
        for (int i = 0; i < names.Length; i++) { Transform t; if (lookup.TryGetValue(Key(names[i]), out t)) return t; }
        return null;
    }

    /// <summary>Splash `count` blood decals on a body location of the victim (the finisher cut calls this). loc: 0 arm, 1 leg, 2 torso, 3 head. Decals are quads parented to the bones.</summary>
    public static void Splash(Transform victim, int loc, int count, GoreCfg g)
    {
        if (victim == null || g == null || !g.enabled || count <= 0 || g.locs == null) return;
        string[] names = { "arm", "leg", "torso", "head" };
        if (loc < 0 || loc >= names.Length) return;
        GoreLocRow gl = null; for (int i = 0; i < g.locs.Length; i++) if (g.locs[i] != null && g.locs[i].name == names[loc]) { gl = g.locs[i]; break; }
        if (gl == null || gl.bones == null || gl.bones.Length == 0) return;
        var lookup = new Dictionary<string, Transform>();
        var all = victim.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++) { string k = Key(all[i].name); if (k.Length > 0 && !lookup.ContainsKey(k)) lookup[k] = all[i]; }
        var rng = new System.Random(g.seed + victim.name.GetHashCode());
        System.Func<float> Rnd = () => (float)rng.NextDouble();
        for (int i = 0; i < count; i++)
        {
            Transform bone = null;
            for (int tries = 0; tries < 4 && bone == null; tries++) bone = FindBone(lookup, new[] { gl.bones[(int)(Rnd() * gl.bones.Length) % gl.bones.Length] });
            if (bone == null) return;
            var mat = SharedDecalMaterial(g, (int)(Rnd() * 4f));
            if (mat == null) return;
            // point on the limb segment (bone -> first child), pushed out to the skin radius on the front arc
            Vector3 a = bone.position, b = bone.childCount > 0 ? bone.GetChild(0).position : a + bone.up * 0.2f;
            Vector3 axis = b - a; if (axis.sqrMagnitude < 1e-6f) axis = bone.up; axis.Normalize();
            float t = Mathf.Lerp(gl.along[0], gl.along[1], Rnd());
            Vector3 p = Vector3.Lerp(a, b, t);
            Vector3 fwd = Vector3.ProjectOnPlane(victim.forward, axis); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(victim.right, axis); fwd.Normalize();
            Vector3 side = Vector3.Cross(axis, fwd);
            float ang = (Rnd() - 0.5f) * Mathf.Min(360f, gl.frontArcDeg) * Mathf.Deg2Rad;
            Vector3 dir = (Mathf.Cos(ang) * fwd + Mathf.Sin(ang) * side).normalized;
            float size = Mathf.Lerp(g.sizeM[0], g.sizeM[1], Rnd()) * 1.75f;   // severity 3 look: the cut is the worst wound

            var go = new GameObject("FinisherBlood_" + names[loc]);
            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            go.transform.position = p + dir * (gl.radiusM + g.quadLift);
            go.transform.rotation = Quaternion.LookRotation(dir, axis) * Quaternion.Euler(0f, 0f, Rnd() * 360f);
            go.transform.localScale = Vector3.one * size;
            go.transform.SetParent(bone, true);   // keeps world pose and size; follows the bone from now on
        }
    }
}
