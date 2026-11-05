using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Runtime state machine of the Hades-gate gateway: ABYSS (default) <-> RIFT (a window into another world, opened on a boss trigger).
// Everything tunable lives in Assets/Data/gateway.json (GatewayTable). HdrpGateway (editor builder) creates the objects and fills the wiring fields below.
//
// Modes:   Auto  = ABYSS until a trigger latches the RIFT open (HP of trigger.hpTarget <= trigger.hpFraction, or a special move landed by trigger.specialActor);
//          Abyss / Rift = forced (gateway.json state.forceState, the command-line arg -gateway:abyss | rift | auto, or debug keys F7 / F8 / F9).
// 'progress' runs 0..1 over transition.openSeconds (closeSeconds going back); every visual is a function of it:
//          fog parts (abyss fog thins and shrinks), _Open dissolve from the centre outward, shock ring, flash, realm-light ramp, shards, camera rumble;
//          a boom + camera kick fire when the opening crosses transition.boomAt.
// Runtime-safe: no UnityEditor API. Time is unscaled so a hit-stop does not stall the transition.
public class GatewayRift : MonoBehaviour
{
    public enum Mode { Auto, Abyss, Rift }

    // ---- wiring (filled by HdrpGateway)
    public Renderer portal;
    public LocalVolumetricFog abyssFog, wispFog;
    public Light depthLight, spillRect, spillSpot, flashLight;
    public ShardOrbit shards;
    public AudioSource boomSource;
    public Texture panorama;
    public Color panoramaAverage = new Color(0.3f, 0.3f, 0.8f, 1f);   // gamma-space average of the panorama, computed by the builder (used when the texture is not readable)

    // ---- live state (inspect in the Editor)
    public Mode mode = Mode.Auto;
    [Range(0f, 1f)] public float progress;
    public bool wantOpen;
    public float targetFraction = 1f;

    GatewayTable.Data G;
    Material mat;
    GatewayCameraShake shake; float shakeSearch;
    Health targetHealth; float pollTimer, findTimer;
    bool latched, suppressed, specialSeen; float deadFor;
    float dissolveCur;
    float flashAge = 999f, prevProgress = -1f, appliedProgress = -2f;
    Color spillColor = Color.white;
    AudioClip boomClip; bool boomWarned;

    // base values captured at Start (the scene / JSON values the dev set; the gateway only multiplies them)
    struct FogBase { public Vector3 size, albedo; public float mfp; }
    FogBase abyssBase, wispBase;
    float depthBase, rectBase, spotBase, flashBase;
    FurnaceFlicker ff; float[] ffLightBase; Color[] ffEmisBase;
    class ScaledLight { public Light light; public Flicker flicker; public float baseValue, abyss, rift; }
    class ScaledFog { public LocalVolumetricFog fog; public Color baseAlbedo; public float abyss, rift; }
    readonly List<ScaledLight> extLights = new List<ScaledLight>();
    readonly List<ScaledFog> extFogs = new List<ScaledFog>();
    readonly List<ParticleSystem> embers = new List<ParticleSystem>(); readonly List<float> emberBase = new List<float>();

    static readonly int OpenId = Shader.PropertyToID("_Open"), ShockId = Shader.PropertyToID("_Shock"), FlashId = Shader.PropertyToID("_Flash"), QuadSizeId = Shader.PropertyToID("_QuadSize");

    static float S01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    static float Ramp(float p, float a, float b) { return S01((p - a) / Mathf.Max(0.0001f, b - a)); }

    public bool IsOpenOrOpening { get { return progress > 0f && wantOpen; } }

    void OnEnable() { ContactRelay.OnEvent += OnCombatEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnCombatEvent; }

    void OnCombatEvent(CombatEvent e)
    {
        if (G == null || G.trigger == null || !G.trigger.specialEnabled || e.actor == null) return;
        if (e.type != "Hit" || string.IsNullOrEmpty(e.moveId)) return;
        if (!string.IsNullOrEmpty(G.trigger.specialMovePrefix) && !e.moveId.StartsWith(G.trigger.specialMovePrefix, StringComparison.Ordinal)) return;
        if (!string.IsNullOrEmpty(G.trigger.specialActor) && e.actor.name != ActorObject(G.trigger.specialActor)) return;
        specialSeen = true;
    }

    // ================================================================ lifecycle
    void Start()
    {
        G = GatewayTable.Get();
        if (G == null || G.portal == null) { enabled = false; return; }

        if (portal != null)
        {
            mat = portal.material;   // per-instance copy: the .mat asset is never touched at run time
            GatewayTable.ApplyMaterialRows(mat, G.portal);
            // look tuning without a rebuild: -gwrot:yaw,pitch[,roll] and -gwint:value override the portal view rotation / intensity
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("-gwrot:")) { var q = arg.Substring(7).Split(','); if (q.Length >= 2 && mat.HasProperty("_Rot")) mat.SetVector("_Rot", new Vector4(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture), q.Length > 2 ? float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture) : 0f, 0f)); }
                if (arg.StartsWith("-gwf:")) { var kv = arg.Substring(5).Split('='); if (kv.Length == 2 && mat.HasProperty(kv[0])) mat.SetFloat(kv[0], float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)); }
                if (arg.StartsWith("-gwint:") && mat.HasProperty("_Intensity")) mat.SetFloat("_Intensity", float.Parse(arg.Substring(7), System.Globalization.CultureInfo.InvariantCulture));
            }
            Vector3 sc = portal.transform.lossyScale;
            if (mat.HasProperty(QuadSizeId)) mat.SetVector(QuadSizeId, new Vector4(sc.x, sc.y, 0f, 0f));
            var fh = G.portal.frameHalf; if (fh != null && fh.Length >= 2 && mat.HasProperty("_FrameHalf")) mat.SetVector("_FrameHalf", new Vector4(fh[0], fh[1], 0f, 0f));
            portal.enabled = false;
        }

        CaptureBases();

        var forced = ResolveMode(); if (forced.HasValue) mode = forced.Value;   // data / arg wins; an empty forceState keeps the inspector value
        float start = mode == Mode.Rift && G.state.instantWhenForced ? 1f : 0f;
        progress = start; wantOpen = start > 0.5f; latched = wantOpen; prevProgress = progress;
        ApplyHeavy(progress);
        if (mode != Mode.Auto) Debug.Log("GATEWAY forced " + mode + " (progress " + progress + ")");
    }

    void CaptureBases()
    {
        if (abyssFog != null) abyssBase = CaptureFog(abyssFog);
        if (wispFog != null) wispBase = CaptureFog(wispFog);
        if (depthLight != null) depthBase = depthLight.intensity;
        if (spillRect != null) rectBase = spillRect.intensity;
        if (spillSpot != null) spotBase = spillSpot.intensity;
        if (flashLight != null) flashBase = flashLight.intensity;
        extLights.Clear(); extFogs.Clear(); embers.Clear(); emberBase.Clear();
        CaptureFurnace();
        CaptureExternal();
        spillColor = ComputeSpillColor();
        if (spillRect != null) spillRect.color = spillColor;
        if (spillSpot != null) spillSpot.color = spillColor;
    }

    /// EDIT-MODE preview for batch captures (HdrpGatewayShot): applies the state at transition progress p (0 abyss .. 1 rift) to the open scene, no Play mode.
    /// Uses a throw-away clone of the portal material and of the chamber emissive materials, so no .mat asset is touched; reopen the scene afterwards to discard.
    public void PreviewState(float p, float flash01)
    {
        if (Application.isPlaying) return;
        G = GatewayTable.Get();
        if (G == null || G.portal == null) return;
        if (portal != null)
        {
            if (previewMat == null) { previewMat = new Material(portal.sharedMaterial); portal.sharedMaterial = previewMat; }
            mat = previewMat;
            var fh = G.portal.frameHalf; if (fh != null && fh.Length >= 2) mat.SetVector("_FrameHalf", new Vector4(fh[0], fh[1], 0f, 0f));
            Vector3 sc = portal.transform.lossyScale; mat.SetVector(QuadSizeId, new Vector4(sc.x, sc.y, 0f, 0f));
        }
        CaptureBases();
        progress = p; wantOpen = p > 0.5f; flashOverride = flash01;
        ApplyHeavy(p);
        ApplyLive(0f);
        if (shards != null) shards.PreviewPose(Ramp(p, G.transition.shardsStart, 1f), 3.7f);
    }
    Material previewMat; float flashOverride = -1f;

    void OnDestroy() { if (mat != null && Application.isPlaying) Destroy(mat); }

    Mode? ResolveMode()
    {
        string v = G.state.forceState;
        string arg = G.state.cmdlineArg;
        if (!string.IsNullOrEmpty(arg))
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith(arg, StringComparison.OrdinalIgnoreCase)) v = a.Substring(arg.Length);
        if (string.IsNullOrEmpty(v)) return null;
        v = v.Trim().ToLowerInvariant();
        return v == "rift" ? Mode.Rift : v == "abyss" ? Mode.Abyss : Mode.Auto;
    }

    static FogBase CaptureFog(LocalVolumetricFog f)
    {
        var p = f.parameters;
        return new FogBase { size = p.size, albedo = new Vector3(p.albedo.r, p.albedo.g, p.albedo.b), mfp = p.meanFreePath };
    }

    void CaptureFurnace()
    {
        ff = FindAnyObjectByType<FurnaceFlicker>();
        if (ff == null) return;
        if (ff.lights != null) { ffLightBase = new float[ff.lights.Length]; for (int i = 0; i < ff.lights.Length; i++) ffLightBase[i] = ff.lights[i] != null ? ff.lights[i].baseIntensity : 0f; }
        if (ff.emissives != null) { ffEmisBase = new Color[ff.emissives.Length]; for (int i = 0; i < ff.emissives.Length; i++) ffEmisBase[i] = ff.emissives[i] != null ? ff.emissives[i].baseEmissive : Color.black; }
        string[] names = G.furnace.emberNames;
        foreach (var ps in ff.GetComponentsInChildren<ParticleSystem>(true))
        {
            bool hit = names == null || names.Length == 0;
            if (!hit) foreach (var n in names) if (ps.name == n) { hit = true; break; }
            if (!hit) continue;
            embers.Add(ps); emberBase.Add(ps.emission.rateOverTimeMultiplier);
        }
    }

    void CaptureExternal()
    {
        if (G.lights.external != null)
            foreach (var r in G.lights.external)
            {
                var go = GameObject.Find(r.name); if (go == null) continue;
                var l = go.GetComponent<Light>(); if (l == null) continue;
                var fl = go.GetComponent<Flicker>();
                extLights.Add(new ScaledLight { light = l, flicker = fl, baseValue = fl != null ? fl.baseIntensity : l.intensity, abyss = r.abyss, rift = r.rift });
            }
        if (G.fog.external != null)
            foreach (var r in G.fog.external)
            {
                var go = GameObject.Find(r.name); if (go == null) continue;
                var f = go.GetComponent<LocalVolumetricFog>(); if (f == null) continue;
                extFogs.Add(new ScaledFog { fog = f, baseAlbedo = f.parameters.albedo, abyss = r.abyss, rift = r.rift });
            }
    }

    Color ComputeSpillColor()
    {
        Color c = GatewayTable.Col(G.spill.color);
        if (G.spill.colorSource == "panorama")
        {
            c = panoramaAverage;
            try
            {
                var t2 = panorama as Texture2D;
                if (t2 != null && t2.isReadable && t2.mipmapCount > 5)
                {
                    var px = t2.GetPixels(t2.mipmapCount - 5);   // about 128 x 64 texels
                    float r = 0f, g = 0f, b = 0f; for (int i = 0; i < px.Length; i++) { r += px[i].r; g += px[i].g; b += px[i].b; }
                    if (px.Length > 0) c = new Color(r / px.Length, g / px.Length, b / px.Length, 1f);
                }
            }
            catch (Exception ex) { Debug.LogWarning("GATEWAY panorama average failed: " + ex.Message); }
        }
        float h, s, v; Color.RGBToHSV(c, out h, out s, out v);
        s = Mathf.Clamp01(s * G.spill.saturation);   // brightness comes from the light intensity, the colour only carries hue + saturation
        c = Color.HSVToRGB(h, s, 1f); c.a = 1f;
        return c;
    }

    // ================================================================ per frame
    void Update()
    {
        if (G == null) return;
        float dt = Time.unscaledDeltaTime;
        if (G.state.debugKeys) DebugKeys();

        wantOpen = ResolveWant(dt);
        float target = wantOpen ? 1f : 0f;
        float dur = wantOpen ? G.transition.openSeconds : G.transition.closeSeconds;
        prevProgress = progress;
        progress = Mathf.MoveTowards(progress, target, dt / Mathf.Max(0.05f, dur));

        if (wantOpen && progress > prevProgress)
        {
            if (prevProgress <= G.transition.boomAt && progress > G.transition.boomAt) Kick();
            else if (G.transition.boomAt <= 0f && prevProgress <= 0f && progress > 0f) Kick();
            if (prevProgress < G.transition.flashAt && progress >= G.transition.flashAt) flashAge = 0f;
        }
        if (flashAge < 999f) flashAge += dt;

        if (progress != appliedProgress) ApplyHeavy(progress);
        ApplyLive(dt);
    }

    void DebugKeys()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F7)) SetMode(Mode.Abyss);
            else if (Input.GetKeyDown(KeyCode.F8)) SetMode(Mode.Rift);
            else if (Input.GetKeyDown(KeyCode.F9)) SetMode(Mode.Auto);
        }
        catch (Exception) { G.state.debugKeys = false; }   // legacy Input Manager disabled: stop polling
    }

    public void SetMode(Mode m)
    {
        mode = m; Debug.Log("GATEWAY mode -> " + m);
        if (m == Mode.Auto) { latched = false; suppressed = false; specialSeen = false; deadFor = 0f; }
    }

    bool ResolveWant(float dt)
    {
        if (mode == Mode.Rift) return true;
        if (mode == Mode.Abyss) return false;

        var T = G.trigger;
        pollTimer -= dt;
        if (pollTimer <= 0f)
        {
            pollTimer = Mathf.Max(0.05f, T.pollSeconds);
            ReadTarget();
        }

        if (!latched)
        {
            if (suppressed) return false;
            bool hp = T.hpEnabled && targetHealth != null && targetFraction <= T.hpFraction && !targetHealth.Dead;
            if (hp || specialSeen) { latched = true; Debug.Log("GATEWAY opening: trigger " + (specialSeen ? "special move" : targetHealth.name + " hp " + targetFraction.ToString("F2"))); }
        }
        else
        {
            if (T.closeOnTargetDeath && targetHealth != null && targetHealth.Dead) { deadFor += dt; if (deadFor >= T.closeDelay) { latched = false; suppressed = true; Debug.Log("GATEWAY closing: " + targetHealth.name + " dead"); } }
            else if (T.hpEnabled && !specialSeen && targetHealth != null && targetFraction > T.hpFraction + T.closeHysteresis) { latched = false; Debug.Log("GATEWAY closing: " + targetHealth.name + " healed to " + targetFraction.ToString("F2")); }
        }
        return latched;
    }

    // gateway.json trigger.hpTarget / specialActor: "enemy" | "player" (the Roster role, so a character swap needs no edit here) or a character id; "" stays "" (any actor)
    static string ActorObject(string who)
    {
        if (string.IsNullOrEmpty(who)) return who;
        if (who == "enemy") return Roster.EnemyObject;
        if (who == "player") return Roster.PlayerObject;
        return Roster.Get(who).objectName;
    }

    void ReadTarget()
    {
        if (targetHealth == null)
        {
            findTimer -= 1f; if (findTimer > 0f && Time.frameCount > 5) return; findTimer = 4f;   // retry every few polls, not every frame
            foreach (var h in FindObjectsByType<Health>()) if (h.name == ActorObject(G.trigger.hpTarget)) { targetHealth = h; break; }
            if (targetHealth == null) return;
        }
        targetFraction = targetHealth.max > 0f ? Mathf.Clamp01(targetHealth.current / targetHealth.max) : 1f;
    }

    // ================================================================ opening kick: boom + shake
    void Kick()
    {
        var B = G.boom; var K = G.shake;
        EnsureShake();
        if (shake != null) shake.Kick(K.kickAmp, K.kickRollDeg, K.kickSeconds, K.kickHz);
        var bus = FindAnyObjectByType<CueBus>();
        if (bus != null && !string.IsNullOrEmpty(B.cue)) bus.Play(B.cue, B.cueGain);   // no-op until the cue exists in CueBus's manifest / sprite
        if (B.procedural && boomSource != null)
        {
            if (boomClip == null) boomClip = MakeBoom(B);
            boomSource.spatialBlend = B.spatialBlend;
            boomSource.PlayOneShot(boomClip, B.gain);
        }
        else if (!boomWarned) { boomWarned = true; Debug.Log("GATEWAY boom skipped: cue '" + B.cue + "' has no clip and procedural boom is off"); }
    }

    void EnsureShake()
    {
        if (shake != null) return;
        if (Time.unscaledTime < shakeSearch) return; shakeSearch = Time.unscaledTime + 1f;
        var cam = Camera.main; if (cam == null) return;
        shake = cam.GetComponent<GatewayCameraShake>();
        if (shake == null) shake = cam.gameObject.AddComponent<GatewayCameraShake>();
    }

    // low sine sweep + filtered-noise rumble, fast attack, exponential decay (placeholder until a real cue exists)
    static AudioClip MakeBoom(GatewayTable.BoomRow b)
    {
        const int sr = 44100;
        int n = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0.2f, b.seconds) * sr));
        var data = new float[n];
        float ph = 0f, lp = 0f; uint s = 987654321u;
        float decay = 5f / Mathf.Max(0.2f, b.seconds);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr, u = t / Mathf.Max(0.2f, b.seconds);
            float f = Mathf.Lerp(b.f0, b.f1, 1f - Mathf.Exp(-4f * u));
            ph += 6.2831853f * f / sr;
            unchecked { s = s * 1664525u + 1013904223u; }
            float white = ((s >> 8) & 0xFFFF) / 32768f - 1f;
            lp += (white - lp) * 0.025f;
            float att = 1f - Mathf.Exp(-t * 260f);
            float tone = Mathf.Sin(ph) + 0.45f * Mathf.Sin(2f * ph + 0.6f) + 0.2f * Mathf.Sin(3.01f * ph);
            float v = (tone * Mathf.Exp(-t * decay) + lp * 6f * b.noiseGain * Mathf.Exp(-t * decay * 0.6f)) * att * 0.55f;
            data[i] = (float)Math.Tanh(v);
        }
        var clip = AudioClip.Create("gateway_boom", n, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ================================================================ apply: slow path (only when progress changed)
    void ApplyHeavy(float p)
    {
        appliedProgress = p;
        var T = G.transition;
        float dissolve = Ramp(p, T.dissolveStart, T.dissolveEnd); if (G.state != null && G.state.abyssReveal > 0f) dissolve = Mathf.Max(dissolve, G.state.abyssReveal * (1f - p));
        float shockT = p > T.shockStart ? Mathf.Clamp01((p - T.shockStart) / Mathf.Max(0.0001f, 1f - T.shockStart)) : 0f;
        if (mat != null) { mat.SetFloat(OpenId, dissolve); mat.SetFloat(ShockId, shockT); }
        dissolveCur = dissolve;
        if (portal != null) portal.enabled = dissolve > 0.0005f;

        // abyss fog parts: thinner, narrower, then switched off (an open rift needs no abyss)
        float part = Ramp(p, 0f, Mathf.Max(0.01f, T.fogPartEnd));
        if (abyssFog != null)
        {
            var row = G.fog.abyss;
            var prm = abyssFog.parameters;
            prm.meanFreePath = Mathf.Lerp(abyssBase.mfp, row.riftMeanFreePath, part);
            Vector3 sc = row.riftSizeScale != null && row.riftSizeScale.Length >= 3 ? GatewayTable.V3(row.riftSizeScale) : Vector3.one;
            prm.size = Vector3.Scale(abyssBase.size, Vector3.Lerp(Vector3.one, sc, part));
            abyssFog.parameters = prm;
            abyssFog.enabled = part < 0.999f;
        }
        if (wispFog != null)
        {
            var row = G.fog.wisp;
            var prm = wispFog.parameters;
            Vector3 rt = row.riftAlbedo != null && row.riftAlbedo.Length >= 3 ? GatewayTable.V3(row.riftAlbedo) : Vector3.one;
            Color tint = new Color(spillColor.r * rt.x, spillColor.g * rt.y, spillColor.b * rt.z, 1f);
            Color baseC = new Color(wispBase.albedo.x, wispBase.albedo.y, wispBase.albedo.z, 1f);
            prm.albedo = Color.Lerp(baseC, tint, Ramp(p, T.spillRampStart, 1f));
            prm.meanFreePath = Mathf.Lerp(wispBase.mfp, row.riftMeanFreePath > 0f ? row.riftMeanFreePath : wispBase.mfp, p);
            wispFog.parameters = prm;
        }

        // furnace interior stays dark and non-emissive: multipliers from gateway.json (hdrp_furnace.json values are untouched)
        var F = G.furnace;
        if (ff != null)
        {
            float ls = Mathf.Lerp(F.lightScaleAbyss, F.lightScaleRift, p), es = Mathf.Lerp(F.chamberEmissiveAbyss, F.chamberEmissiveRift, p);
            if (ff.lights != null && ffLightBase != null)
                for (int i = 0; i < ff.lights.Length && i < ffLightBase.Length; i++)
                    if (ff.lights[i] != null) { ff.lights[i].baseIntensity = ffLightBase[i] * ls; if (!Application.isPlaying && ff.lights[i].light != null) ff.lights[i].light.intensity = ffLightBase[i] * ls; }
            if (ff.emissives != null && ffEmisBase != null)
                for (int i = 0; i < ff.emissives.Length && i < ffEmisBase.Length; i++)
                    if (ff.emissives[i] != null)
                    {
                        var ed = ff.emissives[i];
                        ed.baseEmissive = new Color(ffEmisBase[i].r * es, ffEmisBase[i].g * es, ffEmisBase[i].b * es, 1f);
                        if (!Application.isPlaying && ed.renderers != null && ed.renderers.Length > 0 && ed.renderers[0] != null)   // edit mode: FurnaceFlicker is not running; drive a clone, never the .mat asset
                        {
                            if (ed.material == null) { ed.material = new Material(ed.renderers[0].sharedMaterial); for (int r = 0; r < ed.renderers.Length; r++) if (ed.renderers[r] != null) ed.renderers[r].sharedMaterial = ed.material; }
                            ed.material.SetColor("_EmissiveColor", ed.baseEmissive);
                        }
                    }
            float embs = Mathf.Lerp(F.emberAbyssScale, F.emberRiftScale, p);
            for (int i = 0; i < embers.Count; i++) if (embers[i] != null) { var em = embers[i].emission; em.rateOverTimeMultiplier = emberBase[i] * embs; if (embs <= 0.0001f && embers[i].particleCount > 0) embers[i].Clear(true); }   // scale 0 = embers off: also clear the ones already alive
        }
        for (int i = 0; i < extLights.Count; i++)
        {
            var e = extLights[i]; if (e.light == null) continue;
            float v = e.baseValue * Mathf.Lerp(e.abyss, e.rift, p);
            if (e.flicker != null) e.flicker.baseIntensity = v; else e.light.intensity = v;
        }
        for (int i = 0; i < extFogs.Count; i++)
        {
            var e = extFogs[i]; if (e.fog == null) continue;
            float k = Mathf.Lerp(e.abyss, e.rift, p);
            var prm = e.fog.parameters; prm.albedo = new Color(e.baseAlbedo.r * k, e.baseAlbedo.g * k, e.baseAlbedo.b * k, 1f); e.fog.parameters = prm;
        }

        if (shards != null) shards.SetOpen(Ramp(p, T.shardsStart, 1f));
        if (!Application.isPlaying) return;
        EnsureShake();
        if (shake != null)
        {
            float lvl = (p > 0.001f && p < 0.999f) ? Mathf.Sin(p * Mathf.PI) : 0f;
            shake.SetRumble(lvl, G.shake.rumbleAmp, G.shake.rumbleRollDeg);
        }
    }

    // ================================================================ apply: every frame (cheap: a few light intensities + the flash)
    void ApplyLive(float dt)
    {
        float p = progress, t = Time.time, T0 = G.transition.spillRampStart;
        float spill = Ramp(p, T0, 1f);

        if (depthLight != null)
        {
            var L = G.lights.depth;
            float pulse = 1f + L.pulseAmp * (Mathf.PerlinNoise(3.7f, t * L.pulseHz) * 2f - 1f);
            float k = (1f - Ramp(p, 0f, 0.45f)) * pulse;
            depthLight.intensity = depthBase * k;
            depthLight.enabled = k > 0.001f;
        }
        if (spillRect != null) { float b = 1f + 0.07f * (Mathf.PerlinNoise(11.1f, t * 0.9f) * 2f - 1f); spillRect.intensity = rectBase * spill * b; spillRect.enabled = spill > 0.001f; }
        if (spillSpot != null) { float b = 1f + 0.07f * (Mathf.PerlinNoise(19.3f, t * 0.7f) * 2f - 1f); spillSpot.intensity = spotBase * spill * b; spillSpot.enabled = spill > 0.001f; }

        float fl = 0f;
        if (flashOverride >= 0f) fl = flashOverride;
        else if (flashAge < G.transition.flashSeconds)
        {
            float u = flashAge / Mathf.Max(0.05f, G.transition.flashSeconds);
            fl = Mathf.Min(1f, flashAge / 0.06f) * (1f - u) * (1f - u);
        }
        if (flashLight != null) { flashLight.intensity = flashBase * fl; flashLight.enabled = fl > 0.001f; }
        if (mat != null) mat.SetFloat(FlashId, fl);
        if (portal != null) { bool on = dissolveCur > 0.0005f || fl > 0.001f; if (portal.enabled != on) portal.enabled = on; }
    }
}


