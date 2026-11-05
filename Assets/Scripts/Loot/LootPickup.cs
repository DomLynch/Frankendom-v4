using System.Collections.Generic;
using UnityEngine;

// LOOTUI v85: the world marker of one dropped item. A soft glow pillar (a few crossed vertical quads) and a ground glow, both additive on the project's Furnace/Particle shader
// (the same shader the FX use, known to work in the HDRP player: no new shader to include). The item name in serif is drawn by LootUI (IMGUI, above the pillar).
// Look = loot_ui.json pillar{} + the rarity colour. Lifecycle: grows in (fadeInS) -> idles with a slow pulse -> Collect() flares and fades (flashS) -> destroyed.
public sealed class LootPickup : MonoBehaviour
{
    public string itemId = ""; public GearItem item; public Color color = Color.white;
    public bool Collected { get; private set; }
    public Vector3 Anchor { get { return transform.position; } }   // ground point of the pillar

    Material pillarMat, groundMat; Transform pillarT, groundT; float born, collectedAt, baseIntensity, baseGround;
    static Texture2D pillarTex, groundTex; static readonly Dictionary<int, Mesh> pillarMeshes = new Dictionary<int, Mesh>(); static Mesh groundMesh; static bool shaderWarned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { pillarTex = null; groundTex = null; pillarMeshes.Clear(); groundMesh = null; shaderWarned = false; }

    public static LootPickup Spawn(string id, GearItem it, Vector3 pos)
    {
        var go = new GameObject("LootPickup_" + id); go.transform.position = pos;
        var p = go.AddComponent<LootPickup>(); p.itemId = id; p.item = it; p.color = LootCfg.RarityColor(it); p.Build();
        return p;
    }

    void Build()
    {
        var P = LootCfg.Get().pillar; born = Time.time;
        Shader sh = Shader.Find(P.shader);
        if (sh == null) { if (!shaderWarned) { shaderWarned = true; Debug.LogWarning("[LootUI] pillar shader '" + P.shader + "' not found (add it to Always Included Shaders): the marker has a label only."); } return; }
        if (pillarTex == null) pillarTex = MakePillarTex(P);
        if (groundTex == null) groundTex = MakeGroundTex(P);
        baseIntensity = P.intensity; baseGround = P.groundIntensity;
        pillarMat = MakeMat(sh, pillarTex, P.intensity); groundMat = MakeMat(sh, groundTex, P.groundIntensity);
        pillarT = MakeChild("Pillar", GetPillarMesh(P.planes), pillarMat);
        groundT = MakeChild("Ground", GetGroundMesh(), groundMat);
        groundT.localPosition = new Vector3(0f, P.groundLift, 0f); groundT.localScale = new Vector3(P.groundRadius * 2f, 1f, P.groundRadius * 2f);
        Apply(0f, 1f);
    }

    Material MakeMat(Shader sh, Texture2D tex, float intensity)
    {
        var P = LootCfg.Get().pillar; var m = new Material(sh); m.name = "LootMat";
        m.SetTexture("_MainTex", tex);
        m.SetColor("_Tint", new Color(color.r, color.g, color.b, P.alpha));
        m.SetFloat("_Intensity", intensity); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", P.softDistance);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f);   // additive (One, One), same as fx_materials.json spark
        m.renderQueue = P.renderQueue;
        return m;
    }

    Transform MakeChild(string n, Mesh mesh, Material mat)
    {
        var go = new GameObject(n); go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion; r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return go.transform;
    }

    /// <summary>Pick it up: the pillar flares and fades, then the object goes away.</summary>
    public void Collect() { if (Collected) return; Collected = true; collectedAt = Time.time; }

    void Update()
    {
        var P = LootCfg.Get().pillar; float now = Time.time;
        float grow = Mathf.Clamp01((now - born) / Mathf.Max(0.01f, P.fadeInS)); grow = grow * grow * (3f - 2f * grow);
        float pulse = 1f + P.pulseDepth * Mathf.Sin(now * P.pulseHz * 2f * Mathf.PI);
        if (Collected)
        {
            float k = (now - collectedAt) / Mathf.Max(0.01f, P.flashS);
            if (k >= 1f) { Destroy(gameObject); return; }
            Apply(1f - k, 1f + k * (P.flashHeightMul - 1f), 1f + k * (P.flashWidthMul - 1f), pulse);
        }
        else Apply(grow, Mathf.Lerp(P.riseFrom, 1f, grow), 1f, pulse);
        if (P.spinDegPerS != 0f && pillarT != null) pillarT.Rotate(0f, P.spinDegPerS * Time.deltaTime, 0f, Space.Self);
    }

    void Apply(float fade, float heightK) { Apply(fade, heightK, 1f, 1f); }
    void Apply(float fade, float heightK, float widthK, float pulse)
    {
        if (pillarMat == null) return;
        var P = LootCfg.Get().pillar;
        pillarMat.SetFloat("_Intensity", baseIntensity * fade * pulse); groundMat.SetFloat("_Intensity", baseGround * fade * pulse);
        pillarT.localScale = new Vector3(P.width * widthK, P.height * heightK, P.width * widthK);
        groundT.localScale = new Vector3(P.groundRadius * 2f * widthK, 1f, P.groundRadius * 2f * widthK);
    }

    void OnDestroy() { if (pillarMat != null) Destroy(pillarMat); if (groundMat != null) Destroy(groundMat); }

    // ---- procedural art (no assets) ----
    // pillar: u across (soft sine edge, widthEdgePower), v up (solid at the foot, long fade to nothing at the top)
    static Texture2D MakePillarTex(LootCfg.PillarC P)
    {
        int w = P.texW, h = P.texH; var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LootPillar", hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h; float up = Mathf.Pow(1f - v, P.fadeTopPower); float foot = Mathf.Clamp01(v / Mathf.Max(0.001f, P.fadeBottom));   // foot ramps in so the base is not a hard line
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2f - 1f; float across = Mathf.Pow(Mathf.Clamp01(Mathf.Cos(u * Mathf.PI * 0.5f)), P.widthEdgePower);
                px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(across * up * foot) * 255f));
            }
        }
        t.SetPixels32(px); t.Apply(false, true); return t;
    }
    static Texture2D MakeGroundTex(LootCfg.PillarC P)
    {
        int n = P.groundTex; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LootGround", hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f; float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)), P.groundEdgePower);
            px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        t.SetPixels32(px); t.Apply(false, true); return t;
    }

    // unit meshes, scaled by the transform: the pillar is 1 wide x 1 high (x -0.5..0.5, y 0..1) with N planes crossing at 180/N degrees; the ground is a flat 1 x 1 quad centred on the origin
    static Mesh GetPillarMesh(int planes)
    {
        Mesh m; if (pillarMeshes.TryGetValue(planes, out m) && m != null) return m;
        var vs = new List<Vector3>(); var uv = new List<Vector2>(); var cs = new List<Color32>(); var tris = new List<int>();
        for (int i = 0; i < planes; i++)
        {
            float a = Mathf.PI * i / planes; var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.5f; int b = vs.Count;
            vs.Add(-dir); vs.Add(dir); vs.Add(dir + Vector3.up); vs.Add(-dir + Vector3.up);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
            for (int k = 0; k < 4; k++) cs.Add(new Color32(255, 255, 255, 255));
            tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        m = new Mesh { name = "LootPillarMesh", hideFlags = HideFlags.HideAndDontSave };
        m.SetVertices(vs); m.SetUVs(0, uv); m.SetColors(cs); m.SetTriangles(tris, 0); m.RecalculateBounds();
        var bnd = m.bounds; bnd.Expand(new Vector3(1f, 0f, 1f)); m.bounds = bnd;   // never culled at a steep camera angle
        pillarMeshes[planes] = m; return m;
    }
    static Mesh GetGroundMesh()
    {
        if (groundMesh != null) return groundMesh;
        var m = new Mesh { name = "LootGroundMesh", hideFlags = HideFlags.HideAndDontSave };
        m.SetVertices(new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) });
        m.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
        m.SetColors(new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) });
        m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0); m.RecalculateBounds();
        groundMesh = m; return m;
    }
}
