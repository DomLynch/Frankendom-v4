using System.Collections.Generic;
using UnityEngine;

// P2 ground blood: a few flat, dark, non-glowing splats on the floor beside the victim after a landed Hit (more for heavy blows, kills and low hits; none for a kick).
// No new art: the splat texture is generated in code (like BloodEdge's strips) and drawn on the Furnace/Particle material that FxMaterials already builds for the "blood" role
// (premultiplied alpha, no emission). Everything (counts, sizes, radius, life, colour, pool size) is in Assets/Data/blood.json "ground"; no file or ground.enabled=false = nothing spawns.
// Presentation only: it reads ContactRelay.OnEvent, never touches the sim. Boots itself after the scene loads (no scene edit). The pool is a ring (oldest splat is reused); a new fight (tick goes backwards) clears it.
public class BloodGround : MonoBehaviour
{
    class Splat { public Transform t; public MeshRenderer r; public float born; public float life; public float baseSize; public bool used; }

    static BloodGround instance;
    readonly List<Splat> pool = new List<Splat>();
    Material mat; Texture2D[] tex; Mesh quad; MaterialPropertyBlock mpb; int next, lastTick = -1; uint seed = 29;
    static readonly int TintId = Shader.PropertyToID("_Tint"), MainTexId = Shader.PropertyToID("_MainTex");
    static readonly RaycastHit[] hits = new RaycastHit[8];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var d = BloodTable.Get(); if (!d.enabled || !d.ground.enabled) return;
        var go = new GameObject("BloodGround"); DontDestroyOnLoad(go); instance = go.AddComponent<BloodGround>();
    }

    float Rnd() { unchecked { seed += 0x6d2b79f5u; uint t = (seed ^ (seed >> 15)) * (1u | seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }
    float Between(float lo, float hi) { return lo + (hi - lo) * Rnd(); }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }

    void Awake()
    {
        var g = BloodTable.Get().ground; seed = (uint)Mathf.Max(1, g.seed);
        mpb = new MaterialPropertyBlock();
        mat = FxMaterials.Particle(null, "blood");
        if (mat == null) { Debug.LogWarning("BloodGround: no particle material (FxMaterials), ground blood off."); enabled = false; return; }
        mat.name = "FxMat_bloodGround";
        quad = new Mesh { name = "BloodGroundQuad" };
        quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
        quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 }; quad.RecalculateNormals(); quad.RecalculateBounds();
        tex = new Texture2D[3]; for (int i = 0; i < tex.Length; i++) tex[i] = MakeSplat(Mathf.Max(16, g.texSize), g, i);
        EnsurePool(g);   // PERF: the pooled splat objects (renderers off) are created now instead of in the frame of the first landed hit
    }

    // materials FxPrewarm draws once (sub-pixel) so the first real splat does not compile its pipeline state
    public static void CollectPrewarm(System.Collections.Generic.List<Material> into) { if (instance != null && instance.mat != null && into != null) into.Add(instance.mat); }

    // Soft dark blobs: one main pool, 3-6 satellites and a few pin drops; premultiplied (rgb * a) because the material blends One / OneMinusSrcAlpha.
    Texture2D MakeSplat(int n, BloodTable.GroundCfg g, int variant)
    {
        var px = new float[n * n]; float cx0 = n * 0.5f, cy0 = n * 0.5f;
        int blobs = 4 + (int)(Rnd() * 4f);
        for (int b = 0; b < blobs + 4; b++)
        {
            float ang = Rnd() * Mathf.PI * 2f, dist = b == 0 ? 0f : Between(0.1f, 0.38f) * n, rad = b == 0 ? Between(0.16f, 0.24f) * n : (b < blobs ? Between(0.04f, 0.1f) * n : Between(0.012f, 0.03f) * n);
            float cx = cx0 + Mathf.Cos(ang) * dist, cy = cy0 + Mathf.Sin(ang) * dist; float sx = Between(0.7f, 1.4f);
            int x0 = Mathf.Max(0, (int)(cx - rad * 2f)), x1 = Mathf.Min(n - 1, (int)(cx + rad * 2f)), y0 = Mathf.Max(0, (int)(cy - rad * 2f)), y1 = Mathf.Min(n - 1, (int)(cy + rad * 2f));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                float dx = (x - cx) / (rad * sx), dy = (y - cy) / rad, q = dx * dx + dy * dy;
                if (q < 1f) { float a = 1f - q; a = a * a * (3f - 2f * a); if (a > px[y * n + x]) px[y * n + x] = a; }
            }
        }
        var cols = new Color[n * n]; Color c = new Color(g.color[0], g.color[1], g.color[2], 1f);
        for (int i = 0; i < cols.Length; i++) { float a = Mathf.Clamp01(px[i]) * Mathf.Clamp01(g.alpha); cols[i] = new Color(c.r * a, c.g * a, c.b * a, a); }
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = "BloodSplat" + variant, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.SetPixels(cols); t.Apply(true, true); return t;
    }

    void EnsurePool(BloodTable.GroundCfg g)
    {
        while (pool.Count < Mathf.Max(1, g.max))
        {
            var go = new GameObject("BloodSplat"); go.transform.SetParent(transform, false);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = quad; var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false; mr.enabled = false;
            pool.Add(new Splat { t = go.transform, r = mr });
        }
    }

    Splat Next(BloodTable.GroundCfg g)
    {
        EnsurePool(g);
        var s = pool[next % pool.Count]; next++; return s;
    }

    bool Ground(Vector3 p, out Vector3 point, out Vector3 normal, BloodTable.GroundCfg g)
    {
        point = p; normal = Vector3.up;
        int n = Physics.RaycastNonAlloc(p + Vector3.up * g.rayUp, Vector3.down, hits, g.rayUp + g.rayDown, PhysMasks.NotPhys, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.normal.y < 0.6f) continue;   // walls, stair risers
            if (h.distance < best) { best = h.distance; point = h.point; normal = h.normal; found = true; }
        }
        return found;
    }

    void OnEvent(CombatEvent e)
    {
        if (!enabled || e.type != "Hit" || e.target == null || e.actor == null) return;
        var g = BloodTable.Get().ground; if (!g.enabled) return;
        if (Duel.TickCount < lastTick) Clear(); lastTick = Duel.TickCount;
        bool kill = e.target.Dead; var h = HitReactionTable.Last;
        string w = h != null ? h.weight : (e.heavy ? "heavy" : "light");
        int baseN = kill ? g.kill : w == "heavy" ? g.heavy : w == "kick" ? g.kick : g.light;
        var sc = BloodTable.For(e.target, e.heavy, kill);
        float want = baseN * sc.ground; int n = Mathf.FloorToInt(want) + (Rnd() < want - Mathf.Floor(want) ? 1 : 0);
        if (n <= 0) return;
        Vector3 away = e.target.transform.position - e.actor.transform.position; away.y = 0f; away = away.sqrMagnitude > 1e-4f ? away.normalized : e.target.transform.forward;
        Vector3 feet = e.target.transform.position;
        for (int i = 0; i < n; i++)
        {
            float a = Rnd() * Mathf.PI * 2f; Vector3 rnd = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            float along = Mathf.Max(Vector3.Dot(rnd, away), 0f) + Mathf.Min(Vector3.Dot(rnd, away), 0f) * g.behindMul;   // mostly on the side the blow pushes toward
            Vector3 pos = feet + (rnd * 0.4f + away * (0.6f + along)) * Between(g.minRadius, g.maxRadius);
            if (!Ground(pos, out var gp, out var gn, g)) continue;
            var s = Next(g);
            s.born = Time.unscaledTime; s.life = g.lifeSec; s.baseSize = Between(g.minSize, g.maxSize) * (kill ? g.killSizeMul : 1f) * FighterScale.Of(e.target);
            s.t.position = gp + gn * g.yOffset; s.t.rotation = Quaternion.LookRotation(-gn, rnd) ;   // quad faces up (its +Z points into the ground), random yaw
            s.t.localScale = new Vector3(s.baseSize, s.baseSize * Between(0.8f, 1.25f), 1f);
            mpb.Clear(); mpb.SetTexture(MainTexId, tex[(int)(Rnd() * tex.Length) % tex.Length]); mpb.SetColor(TintId, Color.white);
            s.r.SetPropertyBlock(mpb); s.r.enabled = true; s.used = true;
        }
    }

    // wound drips (WoundSystem): n splats around `feet`, sizeMul scales the usual splat size; same pool, textures, fade and life as hit splats. No instance (blood.json ground off) = nothing.
    public static void Drip(Vector3 feet, int n, float sizeMul)
    {
        if (instance == null || !instance.enabled) return;
        var g = BloodTable.Get().ground; if (!g.enabled) return;
        for (int i = 0; i < n; i++)
        {
            float a = instance.Rnd() * Mathf.PI * 2f; Vector3 rnd = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            Vector3 pos = feet + rnd * instance.Between(0.05f, g.minRadius);
            if (!instance.Ground(pos, out var gp, out var gn, g)) continue;
            var s = instance.Next(g);
            s.born = Time.unscaledTime; s.life = g.lifeSec; s.baseSize = instance.Between(g.minSize, g.maxSize) * sizeMul;
            s.t.position = gp + gn * g.yOffset; s.t.rotation = Quaternion.LookRotation(-gn, rnd);
            s.t.localScale = new Vector3(s.baseSize, s.baseSize * instance.Between(0.8f, 1.25f), 1f);
            instance.mpb.Clear(); instance.mpb.SetTexture(MainTexId, instance.tex[(int)(instance.Rnd() * instance.tex.Length) % instance.tex.Length]); instance.mpb.SetColor(TintId, Color.white);
            s.r.SetPropertyBlock(instance.mpb); s.r.enabled = true; s.used = true;
        }
    }

    void Clear() { for (int i = 0; i < pool.Count; i++) { pool[i].r.enabled = false; pool[i].used = false; } }

    void Update()
    {
        var g = BloodTable.Get().ground; float now = Time.unscaledTime;
        for (int i = 0; i < pool.Count; i++)
        {
            var s = pool[i]; if (!s.used) continue;
            float age = now - s.born, left = s.life - age;
            if (left <= 0f) { s.r.enabled = false; s.used = false; continue; }
            if (left < g.fadeSec)
            {
                float f = Mathf.Clamp01(left / Mathf.Max(0.01f, g.fadeSec)); mpb.Clear(); s.r.GetPropertyBlock(mpb);
                mpb.SetColor(TintId, new Color(f, f, f, f)); s.r.SetPropertyBlock(mpb);
            }
        }
    }
}
