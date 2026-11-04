using UnityEngine;

// Floating obsidian shards orbiting the edge of the gateway frame. Placeholder geometry (a faceted bipyramid, 3 variants) until the Hades env GLB arrives:
// put its Assets path in gateway.json shards.meshPaths and re-run HdrpGateway (the builder copies the first Mesh of each into 'meshes'); nothing here needs to change.
// All numbers come from gateway.json through HdrpGateway / GatewayRift; this component only moves transforms. Runtime-safe (no Editor API). The shards are built in Awake
// (children are not saved in the scene). GatewayRift drives SetOpen(0..1): shards grow out of the frame and spiral in from a wider radius as the rift opens.
public class ShardOrbit : MonoBehaviour
{
    public Mesh[] meshes;                 // optional; empty -> procedural placeholder
    public Material material;             // HDRP Lit dark glossy (made by the builder)
    public int count, seed;
    public Vector2 frameHalf;             // half size of the doorway opening, metres (x right, y up in this transform's space)
    public float marginOut;               // orbit distance outside the frame edge, metres
    public Vector2 sizeRange, speedRange, tumbleRange, zWobble, radialRange;   // metres; deg/s around the frame; deg/s; (amplitude m, Hz); extra radial offset m
    public float introRadiusBoost;        // extra radius while the rift is still opening (m)

    Transform[] shard; float[] ang, spd, size, rad, tum, ph; Vector3[] axis; Quaternion[] q0; MeshRenderer[] rend;
    float open01 = -1f; bool built;

    public void SetOpen(float v) { open01 = Mathf.Clamp01(v); }

    void Awake() { Build(); }

    static float Rand(ref uint s) { unchecked { s = s * 1664525u + 1013904223u; return ((s >> 8) & 0xFFFFFF) / 16777216f; } }
    static float Lerp(Vector2 r, float t) { return r.x + (r.y - r.x) * t; }

    void Build()
    {
        if (built) return; built = true;
        int n = Mathf.Max(0, count);
        shard = new Transform[n]; ang = new float[n]; spd = new float[n]; size = new float[n]; rad = new float[n]; tum = new float[n]; ph = new float[n];
        axis = new Vector3[n]; q0 = new Quaternion[n]; rend = new MeshRenderer[n];
        uint s = (uint)(seed * 7919 + 13);
        Mesh[] ms = (meshes != null && meshes.Length > 0) ? meshes : new[] { MakeShardMesh(1), MakeShardMesh(2), MakeShardMesh(3) };
        for (int i = 0; i < n; i++)
        {
            var go = new GameObject("Shard" + i); go.transform.SetParent(transform, false);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = ms[i % ms.Length];
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
            mr.enabled = false;
            shard[i] = go.transform; rend[i] = mr;
            ang[i] = (i + Rand(ref s) * 0.7f) / n * 360f;
            float dirSign = Rand(ref s) < 0.5f ? 1f : -1f;
            spd[i] = Lerp(speedRange, Rand(ref s)) * dirSign * 0.35f + Lerp(speedRange, Rand(ref s)) * 0.65f;
            size[i] = Lerp(sizeRange, Rand(ref s));
            rad[i] = Lerp(radialRange, Rand(ref s));
            tum[i] = Lerp(tumbleRange, Rand(ref s));
            ph[i] = Rand(ref s) * 6.2831853f;
            axis[i] = new Vector3(Rand(ref s) - 0.5f, Rand(ref s) - 0.5f, Rand(ref s) - 0.5f).normalized;
            if (axis[i].sqrMagnitude < 0.01f) axis[i] = Vector3.up;
            q0[i] = Random.rotationUniform;
        }
    }

    void Update() { Place(Time.time, Time.deltaTime); }

    /// Edit-mode preview (batch captures): builds the children if needed and poses them for a given open amount and time. Reopen the scene afterwards.
    public void PreviewPose(float open, float t) { Build(); SetOpen(open); Place(t, 0f); }

    void Place(float t, float dt)
    {
        if (shard == null) return;
        float o = open01 < 0f ? 0f : open01;
        bool vis = o > 0.002f;
        float hw = frameHalf.x + marginOut, hh = frameHalf.y + marginOut;
        float intro = 1f - Mathf.SmoothStep(0f, 1f, o);   // 1 while opening, 0 when settled
        for (int i = 0; i < shard.Length; i++)
        {
            if (rend[i].enabled != vis) rend[i].enabled = vis;
            if (!vis) continue;
            ang[i] += spd[i] * dt;
            float a = ang[i] * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), sn = Mathf.Sin(a);
            // superellipse (exponent 4) around the frame: a rounded rectangle path
            float x = hw * Mathf.Sign(c) * Mathf.Sqrt(Mathf.Abs(c));
            float y = hh * Mathf.Sign(sn) * Mathf.Sqrt(Mathf.Abs(sn));
            Vector2 nrm = new Vector2(x / (hw * hw), y / (hh * hh)); nrm = nrm.sqrMagnitude > 1e-8f ? nrm.normalized : Vector2.up;
            float r = rad[i] + introRadiusBoost * intro;
            float z = zWobble.x * Mathf.Sin(zWobble.y * t * 6.2831853f + ph[i]);
            shard[i].localPosition = new Vector3(x + nrm.x * r, y + nrm.y * r, z);
            shard[i].localRotation = Quaternion.AngleAxis(tum[i] * t + ph[i] * 57.29578f, axis[i]) * q0[i];
            float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(o * 1.4f - 0.1f * (i % 5)));
            shard[i].localScale = Vector3.one * (size[i] * grow);
        }
    }

    // one flat-shaded triangle; winding is flipped when needed so Unity's clockwise-front rule faces away from the mesh centre
    static void AddFace(Vector3[] v, Vector3[] nn, int[] idx, ref int k, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 centre)
    {
        Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
        Vector3 mid = (p0 + p1 + p2) / 3f;
        if (Vector3.Dot(n, mid - centre) < 0f) { Vector3 t = p1; p1 = p2; p2 = t; n = -n; }
        v[k] = p0; v[k + 1] = p1; v[k + 2] = p2; nn[k] = nn[k + 1] = nn[k + 2] = n;
        idx[k] = k; idx[k + 1] = k + 1; idx[k + 2] = k + 2; k += 3;
    }

    /// Faceted bipyramid, long along Y (flat shaded: every face has its own vertices). 'variant' shifts the equator so the three look different.
    public static Mesh MakeShardMesh(int variant)
    {
        uint s = (uint)(variant * 2654435761u + 5u);
        Vector3 top = new Vector3(0.03f * (Rand(ref s) - 0.5f), 1.0f, 0f), bot = new Vector3(0f, -0.55f - 0.2f * Rand(ref s), 0.03f * (Rand(ref s) - 0.5f));
        var eq = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            float a = (i + 0.25f * (Rand(ref s) - 0.5f)) * Mathf.PI * 0.5f;
            float rr = 0.15f + 0.1f * Rand(ref s);
            eq[i] = new Vector3(Mathf.Cos(a) * rr * (i % 2 == 0 ? 1.5f : 0.8f), 0.12f * (Rand(ref s) - 0.5f), Mathf.Sin(a) * rr * (i % 2 == 0 ? 0.7f : 1.0f));
        }
        var v = new Vector3[24]; var nn = new Vector3[24]; var idx = new int[24]; int k = 0;
        Vector3 centre = new Vector3(0f, 0.1f, 0f);
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = eq[i], b = eq[(i + 1) % 4];
            AddFace(v, nn, idx, ref k, a, b, top, centre);   // upper face
            AddFace(v, nn, idx, ref k, a, bot, b, centre);   // lower face
        }
        var m = new Mesh { name = "GatewayShard" + variant, vertices = v, normals = nn, triangles = idx };
        m.RecalculateBounds();
        return m;
    }
}
