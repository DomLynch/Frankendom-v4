using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// BOSSPHASES C2 (NEW file, UNPROVEN IN UNITY). A ground ring made of one HDRP DecalProjector: the ring decal builder that used to live inside HumbabaWatch (texture, projector set-up),
// now shared. GroundRing.Show(pos, radiusM, seconds, tint) puts a soft ring + faint inner fill on the ground for `seconds` (fade in, hold, fade out, unscaled time so a slow-mo dip does not stretch it).
// Used by BossPhase (boss_phase.json rows[].telegraph{radiusM, seconds}). HumbabaWatch calls BuildRingTexture / CreateProjector with exactly its old numbers (no look change there).
// Nothing is created until Show is called. The decal material is cloned from the existing Resources material "WoundDecalRef" (same as HumbabaWatch); without it Show logs once and returns false.
public static class GroundRing
{
    public class Style
    {
        public string refMaterial = "WoundDecalRef"; public int texSize = 128;
        public float ringRadiusFrac = 0.85f, ringWidthFrac = 0.1f, innerFill = 0.35f, drawDistanceM = 120f, depthM = 6f, maxOpacity = 0.9f;
        public float probeUpM = 0.3f, probeM = 4f, fadeInFrac = 0.2f, fadeOutFrac = 0.35f;   // ground snap: ray from probeUpM above pos, probeM long (starts inside the boss capsule so it only sees the ground)
    }
    public static readonly Style Default = new Style();
    public static int Shown; public static string LastLog = "";   // proof counters (harness / log)

    static readonly int IdBaseColorMap = Shader.PropertyToID("_BaseColorMap"), IdBaseColor = Shader.PropertyToID("_BaseColor");
    static Texture2D whiteRing; static bool warnedMat;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { whiteRing = null; warnedMat = false; Shown = 0; LastLog = ""; }

    // The ring texture (tint in RGB, shape in alpha). Identical algorithm to the old HumbabaWatch.MakeRing.
    public static Texture2D BuildRingTexture(string name, int texSize, float ringRadiusFrac, float ringWidthFrac, float innerFill, Color tint)
    {
        int n = Mathf.Clamp(texSize, 16, 512); var tex = new Texture2D(n, n, TextureFormat.RGBA32, true); tex.name = name; tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[n * n]; float w = Mathf.Max(0.01f, ringWidthFrac);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(u * u + v * v);
                float ring = Mathf.Exp(-((r - ringRadiusFrac) / w) * ((r - ringRadiusFrac) / w)), fill = innerFill * Mathf.Clamp01(1f - r), edge = Mathf.Clamp01((1f - r) / w);
                float al = Mathf.Clamp01(ring + fill) * edge;
                px[y * n + x] = new Color32((byte)Mathf.RoundToInt(tint.r * 255f), (byte)Mathf.RoundToInt(tint.g * 255f), (byte)Mathf.RoundToInt(tint.b * 255f), (byte)Mathf.RoundToInt(al * 255f));
            }
        tex.SetPixels32(px); tex.Apply(true, true); return tex;
    }

    // A decal material cloned from the reference material, with the ring texture and a base colour. null (and one warning) when the reference material is missing.
    public static Material BuildMaterial(string refMaterial, Texture2D tex, Color baseColor, string name)
    {
        var refMat = Resources.Load<Material>(refMaterial);
        if (refMat == null) { if (!warnedMat) { warnedMat = true; Debug.LogWarning("[GroundRing] no decal material '" + refMaterial + "': no ground ring"); } return null; }
        var m = new Material(refMat); m.name = name; m.hideFlags = HideFlags.DontUnloadUnusedAsset; m.EnableKeyword("_COLORMAP"); m.SetTexture(IdBaseColorMap, tex); m.SetColor(IdBaseColor, baseColor);
        return m;
    }

    // One downward DecalProjector, centred on pos, sized radiusM*2 square and depthM deep, fully transparent (fadeFactor 0). Same set-up as the old HumbabaWatch.MakeDecal.
    public static DecalProjector CreateProjector(Material mat, Vector3 pos, float yawDeg, float radiusM, float depthM, float drawDistanceM, string name, out GameObject go)
    {
        go = new GameObject(name); go.transform.position = pos + Vector3.up * (depthM * 0.5f); go.transform.rotation = Quaternion.Euler(90f, yawDeg, 0f);
        var p = go.AddComponent<DecalProjector>(); p.material = mat; p.pivot = Vector3.zero; p.size = new Vector3(radiusM * 2f, radiusM * 2f, depthM); p.drawDistance = drawDistanceM; p.fadeFactor = 0f;
        return p;
    }

    public static bool Show(Vector3 pos, float radiusM, float seconds, Color tint) { return Show(pos, radiusM, seconds, tint, Default); }

    public static bool Show(Vector3 pos, float radiusM, float seconds, Color tint, Style s)
    {
        if (radiusM <= 0f || seconds <= 0f || s == null) return false;
        RaycastHit hit; if (Physics.Raycast(pos + Vector3.up * s.probeUpM, Vector3.down, out hit, s.probeM, ~0, QueryTriggerInteraction.Ignore)) pos.y = hit.point.y;
        if (whiteRing == null) whiteRing = BuildRingTexture("GroundRing", s.texSize, s.ringRadiusFrac, s.ringWidthFrac, s.innerFill, Color.white);
        tint.a = 1f;
        var mat = BuildMaterial(s.refMaterial, whiteRing, tint, "ground_ring"); if (mat == null) return false;
        GameObject go; var proj = CreateProjector(mat, pos, 0f, radiusM, s.depthM, s.drawDistanceM, "GroundRing", out go);
        var fx = go.AddComponent<GroundRingFx>(); fx.proj = proj; fx.mat = mat; fx.t0 = Time.unscaledTime; fx.seconds = seconds; fx.opacity = s.maxOpacity; fx.fadeIn = Mathf.Clamp01(s.fadeInFrac); fx.fadeOut = Mathf.Clamp01(s.fadeOutFrac);
        Shown++; LastLog = "ring r=" + radiusM.ToString("F1") + " s=" + seconds.ToString("F2") + " at " + pos.ToString("F1");
        Debug.Log("[GroundRing] " + LastLog);
        return true;
    }
}

// Fades one ring in and out on unscaled time, then removes the projector and its cloned material.
public sealed class GroundRingFx : MonoBehaviour
{
    public DecalProjector proj; public Material mat; public float t0, seconds, opacity, fadeIn, fadeOut;
    void Update()
    {
        float a = (Time.unscaledTime - t0) / Mathf.Max(0.05f, seconds);
        if (a >= 1f || proj == null) { Destroy(gameObject); return; }
        float k = 1f;
        if (fadeIn > 0f && a < fadeIn) k = a / fadeIn; else if (fadeOut > 0f && a > 1f - fadeOut) k = (1f - a) / fadeOut;
        proj.fadeFactor = Mathf.Clamp01(k) * opacity;
    }
    void OnDestroy() { if (mat != null) Destroy(mat); }
}
