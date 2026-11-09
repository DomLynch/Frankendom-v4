using System;
using System.Collections.Generic;
using UnityEngine;

// laneVIS2 v1, audit item 7 (STAGED for Assets/Scripts/Zones/ZoneMotes.cs; NEVER COMPILED IN UNITY, written CPU-only; Roslyn-checked against the project assemblies only).
// What it is: a thin, slow field of dark dust / ash / spores / red motes per zone, from DATA (zone_motes.json, StreamingAssets first). NOT a screen-filling effect: a few dozen tiny quads (a few pixels each)
// inside a box in front of the camera, drifting at centimetres per second. No glow, no additive: premultiplied alpha, display-referred dark colours (the existing Furnace/Particle "dust" role of FxMaterials,
// so no new shader, material table row or texture: FxMaterials.Particle(null, role) + the soft-dot it already generates).
// One ParticleSystem per zone row, built lazily the first time its zone is the current one; only the current zone's system emits (the others fade their emission out over fadeSeconds and the live motes
// finish their lifetime), so two zones never stack. World simulation space with prewarm, so a camera cut or a free-cam shot shows the field at once; the emitter box follows the camera (position + forward x forwardM),
// so ZoneShot frames and the lock cam see the same density. QualityTiers' ParticleSystem scaling (maxParticles / emission, from first-seen values) applies to these systems like any other, which is wanted.
// Reuse note: there is no generic zone particle reader in the project (GatewayRift embers and FurnaceFlicker fire are one-off); this is the new one. Dark by default: absent file, enabled=false or -zonemotes:off = nothing.
[Serializable] public class ZoneMoteRow
{
    public string id = "", role = "dust"; public float zFrom = -100000f, zTo = 100000f;
    public float[] color = new float[] { 0.2f, 0.2f, 0.2f, 0.3f }, box = new float[] { 20f, 8f, 20f }, drift = new float[] { 0f, 0f, 0f };
    public int count = 40; public float lifeMin = 14f, lifeMax = 22f, sizeMin = 0.02f, sizeMax = 0.05f, forwardM = 8f, bottomM = -0.5f, noiseStrength = 0.2f, noiseFrequency = 0.15f, noiseScroll = 0.1f, softDistance = 0f;
}
[Serializable] public class ZoneMoteData
{
    public bool enabled = false, log = true, prewarm = true;
    public float pollSeconds = 0.25f, fadeSeconds = 3f, fadeInFrac = 0.15f, fadeOutFrac = 0.25f; public int sortingOrder = 0, waitFrames = 40, maxTotalParticles = 400;
    public ZoneMoteRow[] rows = new ZoneMoteRow[0];
}

public sealed class ZoneMotes : MonoBehaviour
{
    sealed class Sys { public ParticleSystem ps; public ParticleSystemRenderer pr; public ZoneMoteRow row; public float baseRate, mul; }
    static readonly float MinLife = 0.1f, MinFade = 0.05f, MinPoll = 0.02f, SqrEps = 0.0001f, GreyDef = 0.2f, AlphaDef = 0.3f; static readonly int MaxLogs = 16, SoftId = Shader.PropertyToID("_SoftDistance"); static readonly Vector3 BoxDef = new Vector3(20f, 8f, 20f);
    static ZoneMotes inst; ZoneMoteData D; readonly List<Sys> sys = new List<Sys>(); readonly HashSet<int> failed = new HashSet<int>(); float next; bool ready; int logged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-zonemotes:", StringComparison.OrdinalIgnoreCase) && (a.EndsWith(":off", StringComparison.OrdinalIgnoreCase) || a.EndsWith(":0"))) return;
        string txt = DataPath.ReadText("zone_motes.json"); if (txt == null) return;
        ZoneMoteData d; try { d = JsonUtility.FromJson<ZoneMoteData>(txt); } catch (Exception e) { Debug.LogWarning("[ZoneMotes] zone_motes.json unreadable, no motes: " + e.Message); return; }
        if (d == null || !d.enabled || d.rows == null || d.rows.Length == 0) return;
        var go = new GameObject("ZoneMotes"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneMotes>(); inst.D = d;
    }

    static Vector3 V(float[] a, Vector3 d) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : d; }
    static Color Col(float[] a) { return a != null && a.Length >= 4 ? new Color(a[0], a[1], a[2], a[3]) : new Color(GreyDef, GreyDef, GreyDef, AlphaDef); }

    void Start() { StartCoroutine(Wait()); }
    System.Collections.IEnumerator Wait() { for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null; ready = true; }

    int ZoneIndex(float z) { int n = D.rows.Length, i = 0; for (; i < n - 1; i++) if (z < D.rows[i].zTo) break; return i; }

    Sys Make(ZoneMoteRow r)
    {
        var mat = FxMaterials.Particle(null, r.role);
        if (mat == null) { Debug.LogWarning("[ZoneMotes] no particle material for role '" + r.role + "' (FxMaterials): row " + r.id + " stays off"); return null; }
        if (r.softDistance > 0f) mat.SetFloat(SoftId, r.softDistance);
        var g = new GameObject("Motes_" + r.id); g.SetActive(false); g.transform.SetParent(transform, false);   // inactive while configured and whenever its zone is not near: enabling it plays it (playOnAwake), prewarmed
        var ps = g.AddComponent<ParticleSystem>();
        var main = ps.main; main.loop = true; main.prewarm = D.prewarm; main.playOnAwake = true; main.simulationSpace = ParticleSystemSimulationSpace.World; main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.startLifetime = new ParticleSystem.MinMaxCurve(r.lifeMin, r.lifeMax); main.startSpeed = 0f; main.startSize = new ParticleSystem.MinMaxCurve(r.sizeMin, r.sizeMax);
        main.startColor = Col(r.color); main.gravityModifier = 0f; main.maxParticles = Mathf.Max(1, Mathf.Min(r.count + r.count / 2, D.maxTotalParticles));
        float rate = r.count / Mathf.Max(MinLife, 0.5f * (r.lifeMin + r.lifeMax));   // steady state count = rate x mean lifetime
        var em = ps.emission; em.enabled = true; em.rateOverTime = rate; em.rateOverDistance = 0f;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = V(r.box, BoxDef); sh.position = new Vector3(0f, 0.5f * sh.scale.y + r.bottomM, 0f);
        var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World; var dr = V(r.drift, Vector3.zero);
        vel.x = dr.x; vel.y = dr.y; vel.z = dr.z;
        var nz = ps.noise; nz.enabled = r.noiseStrength > 0f; nz.strength = r.noiseStrength; nz.frequency = r.noiseFrequency; nz.scrollSpeed = r.noiseScroll; nz.damping = true; nz.quality = ParticleSystemNoiseQuality.Low;
        var col = ps.colorOverLifetime; col.enabled = true;   // fade in and out so a mote never pops: alpha 0 -> 1 over the first fadeInFrac, 1 -> 0 over the last fadeOutFrac of the life
        var gr = new Gradient(); gr.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, D.fadeInFrac), new GradientAlphaKey(1f, 1f - D.fadeOutFrac), new GradientAlphaKey(0f, 1f) });
        col.color = gr;
        var pr = g.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = mat; pr.renderMode = ParticleSystemRenderMode.Billboard; pr.sortingOrder = D.sortingOrder; pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.receiveShadows = false;
        pr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; pr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off; pr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        var s = new Sys { ps = ps, pr = pr, row = r, baseRate = rate, mul = 0f };
        if (D.log && logged++ < MaxLogs) Debug.Log("[ZoneMotes] built " + r.id + ": " + r.count + " motes steady state, box " + sh.scale + ", rate " + rate.ToString("0.00") + "/s");
        return s;
    }

    void Update()
    {
        if (!ready || Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(MinPoll, D.pollSeconds);
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        int cur = live ? ZoneIndex(p.transform.position.z) : -1;
        if (live && sys.Count == 0) for (int i = 0; i < D.rows.Length; i++) sys.Add(null);
        var cam = Camera.main; Vector3 anchor = live ? p.transform.position : Vector3.zero; Vector3 fwd = Vector3.forward;
        if (cam != null) { anchor = cam.transform.position; fwd = cam.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > SqrEps ? fwd.normalized : Vector3.forward; }
        float dt = Mathf.Max(MinPoll, D.pollSeconds), fade = Mathf.Max(MinFade, D.fadeSeconds);
        for (int i = 0; i < sys.Count; i++)
        {
            var row = D.rows[i]; var s = sys[i];
            if (s == null)
            {
                if (i != cur) continue;
                if (failed.Contains(i)) continue; s = Make(row); if (s == null) { failed.Add(i); continue; }
                sys[i] = s;
            }
            float want = i == cur ? 1f : 0f; s.mul = Mathf.MoveTowards(s.mul, want, dt / fade);
            var em = s.ps.emission; em.rateOverTimeMultiplier = s.baseRate * s.mul;
            if (i == cur || s.mul > 0f)
            {
                Vector3 c = anchor + fwd * s.row.forwardM; float gy = live ? p.transform.position.y : anchor.y;
                s.ps.transform.position = new Vector3(c.x, gy, c.z);
                if (!s.ps.gameObject.activeSelf) s.ps.gameObject.SetActive(true);
            }
            else if (s.ps.gameObject.activeSelf && s.ps.particleCount == 0) s.ps.gameObject.SetActive(false);
        }
    }
}
