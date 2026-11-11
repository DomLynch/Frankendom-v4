using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// laneBIRDS v1 (staged DARK, UNPROVEN in Unity): far, sparse crows circling over one landmark per zone (Erlking gibbet zone 6, Grendel palisade zone 4, more rows via data).
// Pure decoration: no collider, no rigidbody, no audio, no shadow casting, nothing reads it. ONE static loader + one hidden MonoBehaviour, ONE dynamic quad mesh (4 verts per bird) drawn with
// Graphics.DrawMesh and ONE material (the project's Furnace/Particle shader: atlas * vertex colour, premultiplied blend). No GameObject per bird.
// Everything is data (birds.json): enabled=false or no file = nothing runs. Harness: -birds:1 forces on, -birds:0 forces off, -birdstime:<s> time offset, -birdsseed:<n> fixed seed.
// Cost is CALCULATED, not measured: per drawn bird 2 sin/cos + 4 vertices on the CPU, one draw call, 2 tris per bird.
[Serializable] public class BrLandmark { public string layout = ""; public string id = ""; public float[] offset = new float[3]; public float[] fallback = new float[3]; }
[Serializable] public class BrFlock
{
    public string id = ""; public string zone = ""; public bool enabled = true; public BrLandmark landmark = new BrLandmark();
    public int countMin = 5, countMax = 12; public float radiusMin = 25f, radiusMax = 60f, heightMin = 25f, heightMax = 45f, speedMin = 5f, speedMax = 9f;
    public float ratioMin = 1f, ratioMax = 1f, wobbleM = 0f, wobbleHz = 0.1f, bobM = 0f, bobHz = 0.2f, ccwFraction = 0.5f, glideFraction = 0f;
}
[Serializable] public class BrCfg
{
    public bool enabled = false, debugLog = false; public string atlas = "Birds/crow_atlas.png", shader = "Furnace/Particle"; public int atlasCols = 2, atlasRows = 2; public int[] cycle = new int[0];
    public float cellInsetPx = 0f, atlasWidthPx = 512f, atlasHeightPx = 256f, contentAspect = 1.5f; public int glideFrame = 1;
    public float wingspanMin = 1.4f, wingspanMax = 2f, flapHzMin = 2f, flapHzMax = 3f, bankDeg = 12f, headingDt = 0.05f;
    public float visibleRadiusM = 140f, maxDistanceM = 140f, farFadeM = 40f, nearHideM = 4f, nearFadeM = 8f, darkenNearM = 40f, darkenFarM = 140f, darkenNear = 1f, darkenFar = 1f;
    public string propTex = "_MainTex", propTint = "_Tint", propIntensity = "_Intensity", propExposure = "_ExposureWeight", propSoft = "_SoftDistance", propSrc = "_SrcBlend", propDst = "_DstBlend";
    public float intensity = 1f, exposureWeight = 0f, softDistanceM = 0f, minAlpha = 0.003f, minAspect = 0.1f;
    public float[] tint = new float[0]; public float timeOffsetS = 0f; public int seed = 0, seedStride = 7919;
    public BrFlock[] flocks = new BrFlock[0];
}
[Serializable] public class BrLyProp { public string id = ""; public float[] pos = new float[3]; }
[Serializable] public class BrLyFile { public BrLyProp[] props = new BrLyProp[0]; }
public sealed class CrowFlocks : MonoBehaviour
{
    class Bird { public float theta0, dir, radius, ratio, yaw, height, omega, flapHz, flapPh, span, wobPh, bobPh; public bool gliding; }
    class Flock { public BrFlock row; public Vector3 centre; public List<Bird> birds = new List<Bird>(); }
    static BrCfg K; static CrowFlocks Inst; static int argOn = -1;
    readonly List<Flock> flocks = new List<Flock>(); Mesh mesh; Material mat; Texture2D tex; readonly List<Vector3> vs = new List<Vector3>(); readonly List<Vector2> uvs = new List<Vector2>(); readonly List<Color> cs = new List<Color>();
    readonly Dictionary<string, BrLyFile> layouts = new Dictionary<string, BrLyFile>();
    static readonly int[] QuadTri = { 0, 1, 2, 0, 2, 3 }; const int QuadVerts = 4;   // geometry constants of a quad, not tuning
    int cap; int lastQuads = -1; float t0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; K = null; argOn = -1; }
    public static BrCfg Get()
    {
        if (K != null) return K; K = new BrCfg(); string txt = DataPath.ReadText("birds.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<BrCfg>(txt); if (k != null) K = k; } catch (Exception e) { Debug.LogWarning("[Birds] birds.json unreadable, off: " + e.Message); K = new BrCfg(); } }
        if (K.flocks == null) K.flocks = new BrFlock[0]; if (K.cycle == null) K.cycle = new int[0];
        return K;
    }
    static string Arg(string key) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return a.Substring(key.Length); return null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; var k = Get(); string a = Arg("-birds:"); argOn = a == null ? -1 : (a.Trim() == "1" ? 1 : 0);
        if (k.debugLog || argOn >= 0) Debug.Log("[Birds] Boot enabled=" + k.enabled + " arg=" + argOn + " harness=" + PerfFlags.HarnessRun());
        if (argOn == 0 || (argOn < 0 && (!k.enabled || PerfFlags.HarnessRun()))) return;
        var go = new GameObject("CrowFlocks"); DontDestroyOnLoad(go); Inst = go.AddComponent<CrowFlocks>();
    }
    Vector3 Resolve(BrLandmark l)
    {
        Vector3 off = V3(l.offset), p = V3(l.fallback); bool found = false;
        if (!string.IsNullOrEmpty(l.layout) && !string.IsNullOrEmpty(l.id))
        {
            BrLyFile f; if (!layouts.TryGetValue(l.layout, out f)) { f = null; try { string t = DataPath.ReadText(l.layout); if (t != null) f = JsonUtility.FromJson<BrLyFile>(t); } catch (Exception e) { Debug.LogWarning("[Birds] layout " + l.layout + " unreadable: " + e.Message); } layouts[l.layout] = f; }
            if (f != null && f.props != null) foreach (var pr in f.props) if (pr != null && pr.id == l.id && pr.pos != null && pr.pos.Length >= 3) { p = V3(pr.pos); found = true; break; }
        }
        if (!found) Debug.LogWarning("[Birds] landmark " + l.id + " not found in " + l.layout + ": using the fallback position");
        return p + off;
    }
    static Vector3 V3(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }
    static float Rand(System.Random r, float lo, float hi) { return lo + (float)r.NextDouble() * (hi - lo); }

    void Start()
    {
        var k = K; float off = k.timeOffsetS; string ta = Arg("-birdstime:"); float tv; if (ta != null && float.TryParse(ta, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tv)) off = tv;
        t0 = off - Time.time; int seed = k.seed; string sa = Arg("-birdsseed:"); int sv; if (sa != null && int.TryParse(sa, out sv)) seed = sv; if (seed == 0) seed = Environment.TickCount;
        int fi = 0;
        foreach (var r in k.flocks)
        {
            fi++; if (r == null || !r.enabled) continue;
            var fl = new Flock { row = r, centre = Resolve(r.landmark) }; var rng = new System.Random(seed + fi * k.seedStride); int n = Mathf.Max(0, rng.Next(Mathf.Min(r.countMin, r.countMax), Mathf.Max(r.countMin, r.countMax) + 1));
            for (int i = 0; i < n; i++)
            {
                var b = new Bird(); b.theta0 = Rand(rng, 0f, Mathf.PI * 2f); b.dir = rng.NextDouble() < r.ccwFraction ? 1f : -1f; b.radius = Rand(rng, r.radiusMin, r.radiusMax); b.ratio = Rand(rng, r.ratioMin, r.ratioMax);
                b.yaw = Rand(rng, 0f, Mathf.PI * 2f); b.height = Rand(rng, r.heightMin, r.heightMax); float sp = Rand(rng, r.speedMin, r.speedMax); b.omega = sp / Mathf.Max(1f, b.radius);
                b.flapHz = Rand(rng, k.flapHzMin, k.flapHzMax); b.flapPh = Rand(rng, 0f, 1f); b.span = Rand(rng, k.wingspanMin, k.wingspanMax); b.wobPh = Rand(rng, 0f, Mathf.PI * 2f); b.bobPh = Rand(rng, 0f, Mathf.PI * 2f);
                b.gliding = rng.NextDouble() < r.glideFraction; fl.birds.Add(b);
            }
            flocks.Add(fl); cap += n;
            if (k.debugLog || argOn >= 0) Debug.Log("[Birds] flock " + r.id + " zone=" + r.zone + " birds=" + n + " centre=" + fl.centre + " seed=" + seed);
        }
        if (cap == 0 || !Setup()) enabled = false;
    }
    bool Setup()
    {
        var k = K; var path = DataPath.Find(k.atlas); if (path == null) { Debug.LogWarning("[Birds] atlas " + k.atlas + " not found: off"); return false; }
        var sh = Shader.Find(k.shader); if (sh == null) { Debug.LogWarning("[Birds] shader " + k.shader + " not found: off"); return false; }
        tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false); if (!tex.LoadImage(System.IO.File.ReadAllBytes(path), false)) { Debug.LogWarning("[Birds] atlas unreadable: off"); return false; }
        tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Trilinear; tex.name = "BirdAtlas";
        mat = new Material(sh); mat.name = "BirdAtlasMat"; mat.SetTexture(k.propTex, tex); var tc = k.tint != null && k.tint.Length >= 3 ? new Color(k.tint[0], k.tint[1], k.tint[2], 1f) : Color.white; mat.SetColor(k.propTint, tc);
        mat.SetFloat(k.propIntensity, k.intensity); mat.SetFloat(k.propExposure, k.exposureWeight); mat.SetFloat(k.propSoft, k.softDistanceM); mat.SetFloat(k.propSrc, (float)BlendMode.One); mat.SetFloat(k.propDst, (float)BlendMode.OneMinusSrcAlpha);
        mesh = new Mesh(); mesh.name = "BirdQuads"; mesh.indexFormat = IndexFormat.UInt16; mesh.MarkDynamic();
        var idx = new int[cap * QuadTri.Length]; for (int i = 0; i < cap; i++) for (int j = 0; j < QuadTri.Length; j++) idx[i * QuadTri.Length + j] = i * QuadVerts + QuadTri[j];
        mesh.vertices = new Vector3[cap * QuadVerts]; mesh.uv = new Vector2[cap * QuadVerts]; mesh.colors = new Color[cap * QuadVerts]; mesh.triangles = idx; return true;
    }
    static float Sm(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / Mathf.Max(Mathf.Epsilon, b - a)); return t * t * (3f - 2f * t); }
    Vector3 PosAt(Flock f, Bird b, float t)
    {
        float th = b.theta0 + b.dir * b.omega * t, r = b.radius + f.row.wobbleM * Mathf.Sin(f.row.wobbleHz * t + b.wobPh), x = r * Mathf.Cos(th), z = r * b.ratio * Mathf.Sin(th), cy = Mathf.Cos(b.yaw), sy = Mathf.Sin(b.yaw);
        return f.centre + new Vector3(x * cy - z * sy, b.height + f.row.bobM * Mathf.Sin(f.row.bobHz * t + b.bobPh), x * sy + z * cy);
    }
    void LateUpdate()
    {
        if (mesh == null) return; var cam = Camera.main; if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0]; if (cam == null) return;
        var k = K; float t = Time.time + t0; Vector3 cp = cam.transform.position; vs.Clear(); uvs.Clear(); cs.Clear(); int cyc = k.cycle.Length; int n = 0;
        float cw = 1f / k.atlasCols, ch = 1f / k.atlasRows, ix = k.cellInsetPx / k.atlasWidthPx, iy = k.cellInsetPx / k.atlasHeightPx, rad = k.bankDeg * Mathf.Deg2Rad;
        foreach (var f in flocks)
        {
            if ((f.centre - cp).sqrMagnitude > k.visibleRadiusM * k.visibleRadiusM) continue;
            foreach (var b in f.birds)
            {
                Vector3 p = PosAt(f, b, t); float d = (p - cp).magnitude; float a = Sm(k.nearHideM, k.nearFadeM, d) * (1f - Sm(k.maxDistanceM - k.farFadeM, k.maxDistanceM, d)); if (a <= k.minAlpha) continue;
                float dk = Mathf.Lerp(k.darkenNear, k.darkenFar, Sm(k.darkenNearM, k.darkenFarM, d));
                Vector3 h = PosAt(f, b, t + k.headingDt) - p; h.y = 0f; if (h.sqrMagnitude < Mathf.Epsilon) h = Vector3.forward; h.Normalize();
                Vector3 right = new Vector3(h.z, 0f, -h.x); float bank = -b.dir * rad; right = right * Mathf.Cos(bank) + Vector3.up * Mathf.Sin(bank);
                float hw = b.span * 0.5f, hh = hw / Mathf.Max(k.minAspect, k.contentAspect);
                int frame; if (b.gliding || cyc == 0) frame = k.glideFrame; else { float ph = (b.flapHz * t + b.flapPh) % 1f; if (ph < 0f) ph += 1f; frame = k.cycle[Mathf.Min(cyc - 1, (int)(ph * cyc))]; }
                int col = frame % k.atlasCols, row = k.atlasRows - 1 - frame / k.atlasCols; float u0 = col * cw + ix, v0 = row * ch + iy, u1 = (col + 1) * cw - ix, v1 = (row + 1) * ch - iy;
                vs.Add(p - right * hw - h * hh); vs.Add(p + right * hw - h * hh); vs.Add(p + right * hw + h * hh); vs.Add(p - right * hw + h * hh);
                uvs.Add(new Vector2(u0, v0)); uvs.Add(new Vector2(u1, v0)); uvs.Add(new Vector2(u1, v1)); uvs.Add(new Vector2(u0, v1)); var c = new Color(dk, dk, dk, a); cs.Add(c); cs.Add(c); cs.Add(c); cs.Add(c); n++;
            }
        }
        if (n == 0 && lastQuads == 0) return;
        var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
        mesh.SetVertices(vs); mesh.SetUVs(0, uvs); mesh.SetColors(cs);
        if (n != lastQuads) { mesh.SetSubMesh(0, new SubMeshDescriptor(0, n * QuadTri.Length), flags); lastQuads = n; }
        mesh.bounds = new Bounds(cp, Vector3.one * (k.maxDistanceM * 2f)); if (n > 0) Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0, null, 0, null, ShadowCastingMode.Off, false);
    }
    void OnDestroy() { if (mesh != null) Destroy(mesh); if (mat != null) Destroy(mat); if (tex != null) Destroy(tex); if (Inst == this) Inst = null; }
}
